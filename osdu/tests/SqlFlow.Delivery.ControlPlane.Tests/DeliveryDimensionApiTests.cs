using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The dimensions of dimension flows as the control plane serves them (docs/dimension-plan.md, Stage 5): the board of every
/// flow and of one, a dimension with its declaration and builds, its members and originals a page at a time in both orders,
/// a member with its originals, filter and history, the change log, the filter of a set of members, the exports, and the
/// builds of a platform run. The builds are written to the ledger as a run writes them, so what these tests hold the API to
/// is what a real build leaves; the tests never reach an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryDimensionApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:*";

    private static readonly DimensionFieldState Field = new("text", "data.Curves", "nested(data.Curves, Mnemonic.keyword)", Repeats: true);

    [Fact]
    public async Task A_dimension_flow_s_dimensions_members_originals_builds_and_filters_are_served_in_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-dimension-" + suffix;
        var partition = "dm" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-dimensions-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var otherPipelineId = CatalogIdentity.Pipeline(repoId, flowName + "-ingestion");
        var yaml = $$"""
            flowType: dimension
            name: {{flowName}}
            description: The curve mnemonics the well logs hold.
            partitions: [{{partition}}]
            parameters:
              logSource: { default: STAT_COMP, description: The log source read. }
            source:
              endpoint: http://localhost
            dimensions:
              - name: CurveMnemonic
                description: Every curve mnemonic, in capitals.
                kind: "{{WellLog}}"
                path: data.Curves.Mnemonic
                clean: [trim, upper]
            """;
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var spec = flow.Dimensions[0];
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-dimensions-" + suffix, RemoteUrl = "https://example/cp-dimensions.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, DimensionFlowDefinition.FlowTypeName, yaml, now));
            db.Pipelines.Add(Pipeline(otherPipelineId, repoId, flowName + "-ingestion", "ingestion", "flowType: ingestion", now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        var dimensionId = 0;
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Dimension, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });

            // The first build found three spellings of GR, DT, and a value cleaning left nothing of.
            var firstRunId = Guid.NewGuid();
            var first = await BuildAsync(ledger, flow, firstRunId, now.AddHours(-3),
                ("GR", "GR", 5), ("gr", "GR", 3), ("Gamma Ray", "GR", 2), ("DT", "DT", 4), ("   ", null, 1));
            dimensionId = first.DimensionId;

            // The second no longer found "Gamma Ray", and found RHOB for the first time.
            var secondRunId = Guid.NewGuid();
            var second = await BuildAsync(ledger, flow, secondRunId, now.AddHours(-2),
                ("GR", "GR", 6), ("gr", "GR", 3), ("DT", "DT", 4), ("RHOB", "RHOB", 9), ("   ", null, 1));

            // The third failed before it wrote, which leaves the dimension as the second left it.
            var (_, failed) = await ledger.StartDimensionRunAsync(Declaration(flow, spec), Guid.NewGuid(), "dimension api tests", now.AddHours(-1));
            await ledger.CloseDimensionRunAsync(failed.DimensionRunId, DimensionRunStatus.Failed, DimensionReadCounts.None, "search answered 503", now.AddHours(-1).AddMinutes(1));

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The board names the flow in the partition asked for, with its dimension as the second build left it and the
            // failed build beside that; the totals count it failing.
            var board = await JsonAsync(client, token, $"/api/v1/delivery/dimensions?partition={partition}");
            Assert.Equal(partition, board.GetProperty("partition").GetString());
            var listed = board.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("name").GetString() == flowName);
            Assert.True(listed.GetProperty("buildsPartition").GetBoolean());
            Assert.Equal(flow.LedgerId, listed.GetProperty("ledgerId").GetGuid());
            Assert.Equal("logSource", listed.GetProperty("parameters")[0].GetProperty("name").GetString());
            var dimension = Assert.Single(listed.GetProperty("dimensions").EnumerateArray());
            Assert.Equal(dimensionId, dimension.GetProperty("dimensionId").GetInt32());
            Assert.Equal(["trim", "upper"], dimension.GetProperty("clean").EnumerateArray().Select(s => s.GetString()));
            Assert.True(dimension.GetProperty("declared").GetBoolean());
            Assert.False(dimension.GetProperty("changed").GetBoolean());
            Assert.Equal((3L, 5L), (dimension.GetProperty("members").GetInt64(), dimension.GetProperty("originals").GetInt64()));
            Assert.Equal(second.DimensionRunId, dimension.GetProperty("current").GetProperty("dimensionRunId").GetInt64());
            Assert.Equal(DimensionRunStatus.Failed, dimension.GetProperty("latest").GetProperty("status").GetString());
            Assert.Equal("search answered 503", dimension.GetProperty("latest").GetProperty("error").GetString());
            Assert.Equal("nested(data.Curves, Mnemonic.keyword)", dimension.GetProperty("field").GetProperty("aggregateBy").GetString());
            var own = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/dimensions?partition={partition}");
            var totals = own.GetProperty("totals");
            Assert.Equal((1, 1, 1, 0), (totals.GetProperty("dimensions").GetInt32(), totals.GetProperty("built").GetInt32(), totals.GetProperty("failing").GetInt32(), totals.GetProperty("notBuilt").GetInt32()));

            // A dimension read alone names the pipeline that builds it, for the page's Build button.
            var detail = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}");
            Assert.Equal(pipelineId, detail.GetProperty("pipelineId").GetGuid());
            Assert.Equal(partition, detail.GetProperty("partition").GetString());
            Assert.Equal("CurveMnemonic", detail.GetProperty("dimension").GetProperty("name").GetString());

            // Members in value order, each with its most common originals beside it.
            var members = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members");
            var items = members.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["DT", "GR", "RHOB"], items.Select(m => m.GetProperty("value").GetString()));
            var gr = items[1];
            Assert.Equal(9, gr.GetProperty("records").GetInt64());
            Assert.Equal([("GR", 6L), ("gr", 3L)], gr.GetProperty("top").EnumerateArray().Select(t => (t.GetProperty("original").GetString(), t.GetProperty("count").GetInt64())));
            Assert.Equal(JsonValueKind.Null, members.GetProperty("next").ValueKind);

            // With the most records first, a page at a time: the cursor of one page starts the next.
            var top = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members?order=records&limit=2");
            Assert.Equal(["GR", "RHOB"], top.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
            var rest = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members?order=records&limit=2&after={Uri.EscapeDataString(top.GetProperty("next").GetString()!)}");
            Assert.Equal(["DT"], rest.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("value").GetString()));

            // A search finds a member by an original it holds now; one no build finds any more only when removed ones are asked.
            Assert.Equal(["GR"], (await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members?search=g")).GetProperty("items").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
            Assert.Empty((await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members?search=gamma")).GetProperty("items").EnumerateArray());
            Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members?search=gamma&removed=true")).GetProperty("items").EnumerateArray());

            // A member's page: its originals, its filter, and the changes that took an original from it.
            var grId = gr.GetProperty("memberId").GetInt64();
            var member = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members/{grId}");
            Assert.Equal(["GR", "gr"], member.GetProperty("originals").EnumerateArray().Select(o => o.GetProperty("original").GetString()));
            var memberFilter = member.GetProperty("filter");
            var expected = DimensionFilters.Of(OsduField.Text("data.Curves.Mnemonic", "data.Curves"), ["GR", "gr"]);
            Assert.Equal(expected, memberFilter.GetProperty("filters").EnumerateArray().Select(f => f.GetString()));
            var history = Assert.Single(member.GetProperty("history").EnumerateArray());
            Assert.Equal(("Gamma Ray", DimensionChangeKinds.Removed), (history.GetProperty("original").GetString(), history.GetProperty("change").GetString()));

            // Originals: those under no member, a member's with the most records first, and every one a page at a time.
            var left = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?leftOut=true");
            Assert.Equal(DimensionLeftOut.Empty, Assert.Single(left.GetProperty("items").EnumerateArray()).GetProperty("leftOut").GetString());
            var ofGr = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?member={grId}&order=count");
            Assert.Equal(["GR", "gr"], ofGr.GetProperty("items").EnumerateArray().Select(o => o.GetProperty("original").GetString()));
            var page = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?limit=3");
            var next = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?limit=3&after={Uri.EscapeDataString(page.GetProperty("next").GetString()!)}");
            Assert.Equal(5, page.GetProperty("items").GetArrayLength() + next.GetProperty("items").GetArrayLength());

            // The builds, newest first, and the change log, newest first, narrowed by build and by kind.
            var builds = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/builds");
            Assert.Equal([failed.DimensionRunId, second.DimensionRunId, first.DimensionRunId], builds.EnumerateArray().Select(b => b.GetProperty("dimensionRunId").GetInt64()));
            Assert.Equal(1, builds[1].GetProperty("changes").GetProperty("originalsAdded").GetInt64());
            var changes = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/changes?build={second.DimensionRunId}");
            Assert.Equal(["Gamma Ray", "RHOB"], changes.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("original").GetString()).Order(StringComparer.Ordinal));
            var added = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/changes?change=added");
            Assert.Equal(("RHOB", "RHOB"), (Assert.Single(added.GetProperty("items").EnumerateArray()).GetProperty("original").GetString(), added.GetProperty("items")[0].GetProperty("toValue").GetString()));

            // The filter of a set of members, named by clean value; a name that is no member is said to be missing.
            var filter = await PostJsonAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/filter", new { values = new[] { "GR", "DT", "NOPE" } });
            Assert.Equal(WellLog, filter.GetProperty("kind").GetString());
            Assert.Equal(["DT", "GR"], filter.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
            Assert.Equal(3, filter.GetProperty("originals").GetInt32());
            Assert.Equal(["NOPE"], filter.GetProperty("missing").EnumerateArray().Select(m => m.GetString()));
            var searched = Assert.Single(filter.GetProperty("searches").EnumerateArray()).GetString()!;
            Assert.Contains("\"gr\"", searched, StringComparison.Ordinal);
            Assert.Contains("\"DT\"", searched, StringComparison.Ordinal);

            // The exports: the members as CSV with a header row, and every original as JSON Lines.
            using (var csv = await SendAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/export?set=members&format=csv"))
            {
                var body = await csv.Content.ReadAsStringAsync();
                Assert.True(csv.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
                var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.StartsWith("member_id,value,records", lines[0], StringComparison.Ordinal);
                Assert.Equal(4, lines.Length);
                Assert.Contains("CurveMnemonic", csv.Content.Headers.ContentDisposition?.FileNameStar ?? csv.Content.Headers.ContentDisposition?.FileName ?? string.Empty, StringComparison.Ordinal);
            }

            using (var jsonl = await SendAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/export?set=originals&format=jsonl"))
            {
                var lines = (await jsonl.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(5, lines.Length);
                Assert.Contains(lines, l => JsonDocument.Parse(l).RootElement.GetProperty("original").GetString() == "   ");
            }

            // The builds a platform run made, named by the dimension each built.
            var ofRun = await JsonAsync(client, token, $"/api/v1/delivery/runs/{secondRunId}/dimension-builds");
            Assert.Equal("CurveMnemonic", Assert.Single(ofRun.EnumerateArray()).GetProperty("dimension").GetString());

            // What the API cannot answer, it says why.
            await ProblemAsync(client, token, "/api/v1/delivery/dimensions/2147483000", HttpStatusCode.NotFound, "No dimension");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members?order=size", HttpStatusCode.BadRequest, "not one of value, records");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/members?after=not-a-cursor", HttpStatusCode.BadRequest, "is not a cursor");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/values?member={grId}&leftOut=true", HttpStatusCode.BadRequest, "not both");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/changes?change=renamed", HttpStatusCode.BadRequest, "not one of added");
            await ProblemAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/export?format=pdf", HttpStatusCode.BadRequest, "not one of csv, jsonl");
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{otherPipelineId}/dimensions", HttpStatusCode.Conflict, "not a dimension flow");
            using (var none = await PostAsync(client, token, $"/api/v1/delivery/dimensions/{dimensionId}/filter", new { values = Array.Empty<string>() }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
                Assert.Contains("at least one member", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryDimensionChanges.Where(c => c.DimensionId == dimensionId).ExecuteDeleteAsync();
                await osdu.DeliveryDimensionValues.Where(v => v.DimensionId == dimensionId).ExecuteDeleteAsync();
                await osdu.DeliveryDimensionMembers.Where(m => m.DimensionId == dimensionId).ExecuteDeleteAsync();
                await osdu.DeliveryDimensionRuns.Where(r => r.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryDimensions.Where(d => d.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryLedgers.Where(l => l.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryLedgerPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    private static DimensionDeclaration Declaration(DimensionFlowDefinition flow, DimensionSpec spec) => new()
    {
        FlowId = flow.LedgerId,
        FlowName = flow.Name,
        Name = spec.Name,
        Description = spec.Description,
        Kind = spec.Kind,
        Path = spec.Path,
        CleanJson = """[{"kind":"trim"},{"kind":"upper"}]""",
        DefinitionHash = spec.DefinitionHash,
    };

    /// <summary>
    /// Writes a completed build as the runner does: registered, then its originals (original, clean value or none, count) and
    /// the members they add up to, each member's filter written by the filters the API writes too.
    /// </summary>
    private static async Task<DimensionRunState> BuildAsync(
        OsduLedger ledger, DimensionFlowDefinition flow, Guid runId, DateTime startedUtc, params (string Original, string? Clean, long Count)[] found)
    {
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(flow, flow.Dimensions[0]), runId, "dimension api tests", startedUtc);
        var field = OsduField.Text("data.Curves.Mnemonic", "data.Curves");
        var originals = found
            .Select(f => new DimensionOriginalWrite(f.Original, f.Clean, f.Clean is null ? DimensionLeftOut.Empty : null, null, f.Count, Filterable: true))
            .ToList();
        var members = originals.Where(o => o.CleanValue is not null)
            .GroupBy(o => o.CleanValue!, StringComparer.Ordinal)
            .Select(g => new DimensionMemberWrite(g.Key, g.Sum(o => o.Count), RecordsExact: false, g.Count(), 0, DimensionFilters.Of(field, g.Select(o => o.Original).ToList())[0], 1))
            .ToList();
        return await ledger.WriteDimensionAsync(new DimensionWrite
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = flow.LedgerId,
            Field = Field,
            Originals = originals,
            Members = members,
            Read = new DimensionReadCounts { Records = 20, WithValue = 19, Aggregations = 1, Slices = 1 },
            CompletedUtc = startedUtc.AddMinutes(1),
        });
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, string yaml, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = kind,
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"{{kind}}"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static async Task ProblemAsync(HttpClient client, string token, string path, HttpStatusCode status, string says)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"GET {path} answered {(int)response.StatusCode}, not {(int)status}: {body}");
        Assert.Contains(says, body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string token, string path, object body)
    {
        using var response = await PostAsync(client, token, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {path} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
