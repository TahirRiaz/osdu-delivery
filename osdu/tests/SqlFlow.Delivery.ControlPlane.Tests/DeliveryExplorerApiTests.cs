using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The explorer as the API serves it (osdu/docs/explorer.md): the connection each partition is read through, picked from the
/// delivery flows that reach it (the storage route first, a flow whose partition is its header's by the partition it names),
/// none for a partition no flow reaches; each read run by the control plane through that connection with what it reads, and
/// nothing queued for a node; and every search the operation would refuse answered as a 400 before it runs. Nothing here
/// reaches an OSDU: the operations are stood in for (<see cref="RecordedOperations"/>), and what each was given is read back.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryExplorerApiTests
{
    [Fact]
    public async Task Each_partition_is_read_through_one_flows_connection_and_a_search_the_operation_would_refuse_is_refused_here()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (named, headed, unreached) = ("p" + suffix, "h" + suffix, "z" + suffix);
        var repoId = FlowIdentity.FromName("repo/cp-explorer-" + suffix);
        // Named so the DDMS flow comes first by name: the storage flow is picked for the route it delivers by.
        var ddms = Flow("explorer-a-" + suffix, $"partitions: [{named}]", "ddms", "protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }");
        var storage = Flow("explorer-b-" + suffix, $"partitions: [{named}]", "storage");
        var header = Flow("explorer-c-" + suffix, null, "storage", $"headers: {{ data-partition-id: {headed} }}");
        var now = DateTime.UtcNow;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-explorer-" + suffix, RemoteUrl = "https://example/cp-explorer.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            foreach (var (name, yaml) in new[] { ddms, storage, header })
            {
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, name),
                    RepoId = repoId,
                    Name = name,
                    Kind = "delivery",
                    RelativePath = "flows/" + name + ".yaml",
                    ContentHash = new string('0', 64),
                    Yaml = yaml,
                    DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"delivery"}"""),
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
            }

            await db.SaveChangesAsync();
        }

        var operations = new RecordedOperations();
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The DDMS flow is a flow the catalog reads, so it was weighed and passed over for its route, not skipped.
            using (var parsed = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{CatalogIdentity.Pipeline(repoId, ddms.Name):D}/interfaces"))
            {
                Assert.Equal(HttpStatusCode.OK, parsed.StatusCode);
            }

            var viaStorage = await ConnectionAsync(client, token, $"?partition={named}", null);
            Assert.True(viaStorage.GetProperty("available").GetBoolean());
            Assert.Equal((named, storage.Name, "storage", "${env:OSDU_URL}"), (
                viaStorage.GetProperty("partition").GetString(), viaStorage.GetProperty("through").GetString(),
                viaStorage.GetProperty("route").GetString(), viaStorage.GetProperty("endpoint").GetString()));

            // The workbench's partition, carried in its header, is the one read when the request names none.
            var viaHeader = await ConnectionAsync(client, token, string.Empty, headed);
            Assert.Equal((headed, header.Name), (viaHeader.GetProperty("partition").GetString(), viaHeader.GetProperty("through").GetString()));

            var none = await ConnectionAsync(client, token, $"?partition={unreached}", null);
            Assert.False(none.GetProperty("available").GetBoolean());
            Assert.Contains($"No delivery flow reaches partition '{unreached}'", none.GetProperty("reason").GetString(), StringComparison.Ordinal);

            // A search runs as asked, through the storage flow, in the partition it was asked in.
            var searched = await RanAsync(client, token, $"/api/v1/delivery/explorer/search?partition={named}", new
            {
                text = "NO 33",
                kind = "*:*:master-data--Wellbore:*",
                filters = new[] { new { path = "legal.legaltags", index = "keyword", value = "dev-private" } },
                sort = "Modified",
                offset = 100,
                limit = 100,
                facet = new { path = "data.FacilityTypeID" },
            });
            Assert.Equal((ExploreOperation.OperationName, storage.Name), (searched.Operation, searched.SourceRef));
            Assert.Equal((ExploreOperation.SearchAction, named), (searched.Argument(ExploreOperation.ActionArgument), searched.Argument(DeliveryOperation.PartitionArgument)));
            var search = ExplorerSearch.Parse(searched.Argument(ExploreOperation.SearchArgument));
            Assert.Equal(("NO 33", "*:*:master-data--Wellbore:*", ExplorerSort.Modified, 100, 100), (search.Text, search.Kind, search.Sort, search.Offset, search.Limit));
            Assert.Equal(new ExplorerFilter { Path = "legal.legaltags", Index = OsduFieldIndex.Keyword, Value = "dev-private" }, Assert.Single(search.Filters));
            Assert.Equal(new ExplorerField { Path = "data.FacilityTypeID", Index = OsduFieldIndex.Text }, search.Facet);

            var types = await RanAsync(client, token, $"/api/v1/delivery/explorer/types?partition={named}", new { text = "NO 33" });
            Assert.Equal(ExploreOperation.TypesAction, types.Argument(ExploreOperation.ActionArgument));
            var fields = await RanAsync(client, token, $"/api/v1/delivery/explorer/fields?partition={named}", new { kind = "osdu:wks:master-data--Wellbore:1.1.0" });
            Assert.Equal((ExploreOperation.FieldsAction, "osdu:wks:master-data--Wellbore:1.1.0"), (fields.Argument(ExploreOperation.ActionArgument), fields.Argument(ExploreOperation.KindArgument)));

            // A record is checked against what the Schema service holds by default, or a saved template's version asked for.
            var validated = await RanAsync(client, token, $"/api/v1/delivery/explorer/validate?partition={named}", new { targetId = "dev:master-data--Wellbore:NO-33:", version = 7L });
            Assert.Equal(
                (ExploreOperation.ValidateAction, "dev:master-data--Wellbore:NO-33", "7", (string?)null, (string?)null),
                (validated.Argument(ExploreOperation.ActionArgument), validated.Argument("targetId"), validated.Argument("version"),
                    validated.Argument(ExploreOperation.SchemaArgument), validated.Argument(ExploreOperation.TemplateVersionArgument)));
            var againstSaved = await RanAsync(client, token, $"/api/v1/delivery/explorer/validate?partition={named}", new { targetId = "dev:master-data--Wellbore:NO-33", schema = " Saved ", templateVersion = " 9f3c41d07a2b88e1 " });
            Assert.Equal(("saved", "9f3c41d07a2b88e1"), (againstSaved.Argument(ExploreOperation.SchemaArgument), againstSaved.Argument(ExploreOperation.TemplateVersionArgument)));

            // A check of a search reads from the first record whatever page the search was on, up to the most asked.
            var listed = await RanAsync(client, token, $"/api/v1/delivery/explorer/validate-list?partition={named}", new
            {
                search = new { text = "NO 33", kind = "*:*:master-data--Wellbore:*", offset = 300, limit = 50, facet = new { path = "data.FacilityTypeID" } },
                max = 250,
            });
            Assert.Equal((ExploreOperation.ValidateListAction, "250"), (listed.Argument(ExploreOperation.ActionArgument), listed.Argument(ExploreOperation.MaxArgument)));
            var checkedSearch = ExplorerSearch.Parse(listed.Argument(ExploreOperation.SearchArgument));
            Assert.Equal(("NO 33", "*:*:master-data--Wellbore:*", 0, ExplorerSearch.DefaultLimit), (checkedSearch.Text, checkedSearch.Kind, checkedSearch.Offset, checkedSearch.Limit));
            Assert.Null(checkedSearch.Facet);
            var everything = await RanAsync(client, token, $"/api/v1/delivery/explorer/validate-list?partition={named}", new { });
            Assert.Equal(ExplorerChecks.MaxRecords.ToString(CultureInfo.InvariantCulture), everything.Argument(ExploreOperation.MaxArgument));

            var validate400 = $"/api/v1/delivery/explorer/validate?partition={named}";
            await RefusedAsync(client, token, validate400, new { targetId = "dev:master-data--Wellbore:NO-33", schema = "remote" }, HttpStatusCode.BadRequest, "is not a schema a record is checked against");
            await RefusedAsync(client, token, validate400, new { targetId = "NO 33/9-C-28 B" }, HttpStatusCode.BadRequest, "is not an OSDU record id");
            await RefusedAsync(client, token, validate400, new { targetId = "dev:master-data--Wellbore:NO-33", schema = "saved", templateVersion = new string('v', 65) }, HttpStatusCode.BadRequest, "saved template version");
            var list400 = $"/api/v1/delivery/explorer/validate-list?partition={named}";
            await RefusedAsync(client, token, list400, new { max = 1001 }, HttpStatusCode.BadRequest, "A check reads 1 to 1000 records");
            await RefusedAsync(client, token, list400, new { max = 0 }, HttpStatusCode.BadRequest, "A check reads 1 to 1000 records");
            await RefusedAsync(client, token, list400, new { search = new { kind = "osdu:wks" } }, HttpStatusCode.BadRequest, "is not a kind");
            await RefusedAsync(client, token, list400, new { schema = "remote" }, HttpStatusCode.BadRequest, "is not a schema a record is checked against");

            // A read takes a reference as a document holds it, and a version beside it; a flow whose partition is its header's
            // is told no partition, since its header already names it.
            var read = await RanAsync(client, token, "/api/v1/delivery/explorer/read", new { targetId = $" {headed}:master-data--Wellbore:NO-33: ", version = 1712345678901234L }, headed);
            Assert.Equal((ExploreOperation.ReadAction, header.Name), (read.Argument(ExploreOperation.ActionArgument), read.SourceRef));
            Assert.Equal(($"{headed}:master-data--Wellbore:NO-33", "1712345678901234"), (read.Argument("targetId"), read.Argument("version")));
            Assert.Null(read.Argument(DeliveryOperation.PartitionArgument));

            var search400 = $"/api/v1/delivery/explorer/search?partition={named}";
            await RefusedAsync(client, token, search400, new { kind = "osdu:wks" }, HttpStatusCode.BadRequest, "is not a kind");
            await RefusedAsync(client, token, search400, new { sort = "newest" }, HttpStatusCode.BadRequest, "is not an order");
            await RefusedAsync(client, token, search400, new { sort = "1" }, HttpStatusCode.BadRequest, "is not an order");
            await RefusedAsync(client, token, search400, new { filters = new[] { new { path = "data.Name", index = "fuzzy", value = "x" } } }, HttpStatusCode.BadRequest, "is not how a property is indexed");
            await RefusedAsync(client, token, search400, new { filters = new[] { new { path = "data.Name", index = "text", value = new string('x', 300) } } }, HttpStatusCode.BadRequest, "cannot narrow to data.Name");
            await RefusedAsync(client, token, search400, new { offset = 9950, limit = 100 }, HttpStatusCode.BadRequest, "first 10,000 records");
            await RefusedAsync(client, token, search400, new { text = "a", mentions = "dev:master-data--Well:1" }, HttpStatusCode.BadRequest, "not both");
            await RefusedAsync(client, token, $"/api/v1/delivery/explorer/fields?partition={named}", new { kind = " " }, HttpStatusCode.BadRequest, "Name the kind");
            await RefusedAsync(client, token, "/api/v1/delivery/explorer/read?partition=" + named, new { targetId = "NO 33/9-C-28 B" }, HttpStatusCode.BadRequest, "is not an OSDU record id");

            // A partition no flow reaches has no connection to read through.
            await RefusedAsync(client, token, $"/api/v1/delivery/explorer/search?partition={unreached}", new { text = "x" }, HttpStatusCode.Conflict, "No delivery flow reaches partition");
            Assert.Empty(operations.Given);

            // Every read was answered in the request: nothing was queued for a node.
            await using var catalog = CatalogDatabase.Create(cs);
            var flows = new[] { ddms.Name, storage.Name, header.Name };
            Assert.False(await catalog.ComputeTasks.AnyAsync(t => flows.Contains(t.SourceRef)));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var names = new[] { ddms.Name, storage.Name, header.Name };
            await db.ComputeTasks.Where(t => names.Contains(t.SourceRef)).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }

        // The operation's own answer is the response, and the payload it was run with is what a node would have been given.
        async Task<ComputeTaskPayload> RanAsync(HttpClient client, string token, string path, object? body, string? workbench = null)
        {
            using var response = await SendAsync(client, token, HttpMethod.Post, path, body, workbench);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {path} answered {(int)response.StatusCode}: {text}");
            var payload = operations.Last();
            Assert.Equal(payload.Operation, JsonDocument.Parse(text).RootElement.GetProperty("ran").GetString());
            return payload;
        }
    }

    /// <summary>A delivery flow of one interface reading a table no test opens, by the route and partition settings given.</summary>
    private static (string Name, string Yaml) Flow(string name, string? partitions, string protocol, string? targetExtra = null)
        => (name, $$"""
            flowType: delivery
            name: {{name}}
            {{partitions ?? string.Empty}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/explorer
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: {{protocol}}
              {{targetExtra ?? string.Empty}}
            """);

    private static async Task<JsonElement> ConnectionAsync(HttpClient client, string token, string query, string? workbench)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, "/api/v1/delivery/explorer/connection" + query, null, workbench);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET connection{query} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task The_queries_of_an_element_are_read_from_the_saved_template_and_nothing_is_asked_of_OSDU()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.SaveTemplatesAsync(cs);
        var operations = new RecordedOperations();
        await using var factory = new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
            .WithServices(services => services.AddSingleton(operations.Registry()));
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        // A text of a wellbore: its whole value by its keyword, its words, and whether a record holds one.
        using (var answered = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/element-queries", new
        {
            kind = SampleEstate.WellboreTemplateKind,
            path = "data.FacilityName",
            value = "NO 16/2-9 S",
        }))
        {
            var text = await answered.Content.ReadAsStringAsync();
            Assert.True(answered.StatusCode == HttpStatusCode.OK, text);
            var answer = JsonDocument.Parse(text).RootElement;
            var queries = answer.GetProperty("queries").EnumerateArray().ToDictionary(q => q.GetProperty("purpose").GetString()!, q => q.GetProperty("query").GetString());
            Assert.Equal("data.FacilityName.keyword:\"NO 16/2-9 S\"", queries["exact"]);
            Assert.Equal("_exists_:data.FacilityName", queries["exists"]);
            Assert.Equal(SampleEstate.WellboreTemplateKind, answer.GetProperty("template").GetProperty("kind").GetString());
            Assert.Equal("text", answer.GetProperty("field").GetProperty("index").GetString());
        }

        // A value of a nested list, by its place in the record, is asked inside nested(...).
        using (var nested = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/element-queries", new
        {
            kind = SampleEstate.WellboreTemplateKind,
            path = "data.NameAliases[1].AliasName",
            value = "NO 16/2-9 S",
        }))
        {
            var answer = JsonDocument.Parse(await nested.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(
                "nested(data.NameAliases, (AliasName.keyword:\"NO 16/2-9 S\"))",
                answer.GetProperty("queries").EnumerateArray().Single(q => q.GetProperty("purpose").GetString() == "exact").GetProperty("query").GetString());
        }

        // An item of a nested list, by the values it holds: all of them together inside one nested query.
        using (var item = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/element-queries", new
        {
            kind = SampleEstate.WellboreTemplateKind,
            path = "data.NameAliases[0]",
            section = true,
            values = new object[]
            {
                new { path = "data.NameAliases[0].AliasName", value = "NO 16/2-9 S" },
                new { path = "data.NameAliases[0].AliasNameTypeID", value = "dev:reference-data--AliasNameType:Borehole:" },
            },
        }))
        {
            var answer = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(
                "nested(data.NameAliases, (AliasName.keyword:\"NO 16/2-9 S\" AND AliasNameTypeID.keyword:\"dev:reference-data--AliasNameType:Borehole:\"))",
                answer.GetProperty("queries")[0].GetProperty("query").GetString());
        }

        await RefusedAsync(client, token, "/api/v1/delivery/explorer/element-queries", new { kind = "osdu:wks:WellLog", path = "data.X" }, HttpStatusCode.BadRequest, "osdu:wks:WellLog");
        await RefusedAsync(client, token, "/api/v1/delivery/explorer/element-queries", new { kind = SampleEstate.WellboreTemplateKind }, HttpStatusCode.BadRequest, "Name the element's path");
        await RefusedAsync(client, token, "/api/v1/delivery/explorer/element-queries", new { kind = SampleEstate.WellboreTemplateKind, path = "data.X", value = new { a = 1 } }, HttpStatusCode.BadRequest, "is a section");
        Assert.Empty(operations.Given);
    }

    private static async Task RefusedAsync(HttpClient client, string token, string path, object body, HttpStatusCode status, string why)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"POST {path} answered {(int)response.StatusCode}: {text}");
        Assert.Contains(why, text, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null, string? workbench = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (workbench is not null)
        {
            request.Headers.Add("X-Osdu-Partition", workbench);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
