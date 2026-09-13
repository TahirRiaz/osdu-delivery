using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Templates and the mapping builder through the API (docs/delivery/mapping-templates.md): saving a template version, which
/// takes a signed-in caller, reading it as variables and as its schema, the delete a pinned version refuses, and the builder's
/// repository listing, draft, parse and check against a repository's cache as the catalog carries it. Browsing OSDU is
/// queued as node operations, which the in-process worker leaves queued here. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeliveryTemplateApiTests
{
    private const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string WellLogVersion = "26a3c3441882db4f";
    private const string WellLogFile = "osdu_wks_work-product-component--WellLog_1.4.0.json";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string WellboreVersion = "a110ad82c3b60a1e";
    private const string WellboreFile = "osdu_wks_master-data--Wellbore_1.3.0.json";
    private const string ReferenceVersion = "20260908T212727Z";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string SampleRoot => Path.Combine(AppContext.BaseDirectory, "samples");

    [SkippableFact]
    public async Task A_template_is_saved_once_by_a_signed_in_caller_and_read_as_variables_and_as_its_schema()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var reader = await TokenAsync(client, "read");
        var author = await TokenAsync(client, "read", "author");

        // Saving is an author action, which every signed-in user may take and no anonymous caller may.
        using (var anonymous = await client.PostAsJsonAsync(new Uri("/api/v1/delivery/templates", UriKind.Relative), SaveBody(WellboreKind, WellboreFile), Web))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var first = await ReadAsync<DeliveryTemplateSavedDto>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", SaveBody(WellboreKind, WellboreFile)));
        Assert.Equal(WellboreVersion, first.Template.Version);
        Assert.Contains(first.Outcome, new[] { "created", "unchanged" });
        var again = await ReadAsync<DeliveryTemplateSavedDto>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", SaveBody(WellboreKind, WellboreFile)));
        Assert.Equal("unchanged", again.Outcome);

        var listed = await ReadAsync<List<DeliveryTemplateDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/templates"));
        Assert.Contains(listed, t => t.Kind == WellboreKind && t.Version == WellboreVersion);

        var detail = await ReadAsync<DeliveryTemplateDetailDto>(await SendAsync(client, reader, HttpMethod.Get, DetailUrl(WellboreKind, WellboreVersion)));
        Assert.NotNull(detail.Saved);
        Assert.Contains(detail.Variables, v => v.Path == "osdu.data.FacilityName" && v.Role == "Mapping");
        Assert.Contains(detail.Variables, v => v.Path == "osdu.id" && v.Role == "Engine");
        Assert.Contains(detail.Variables, v => v.Path == "osdu.data.NameAliases" && v.Shape == "GroupList");

        using (var schema = await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/templates/schema?kind={Uri.EscapeDataString(WellboreKind)}&version={WellboreVersion}"))
        {
            Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
            Assert.Equal("application/json", schema.Content.Headers.ContentType?.MediaType);
            Assert.NotNull(JsonNode.Parse(await schema.Content.ReadAsStringAsync())?["properties"]?["data"]);
        }

        using var notRecord = await SendAsync(client, reader, HttpMethod.Post, "/api/v1/delivery/templates/preview", new { kind = WellboreKind, schema = new { type = "object" } });
        Assert.Equal(HttpStatusCode.BadRequest, notRecord.StatusCode);
        Assert.Contains("declares no 'data' property", await notRecord.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_template_version_a_synced_mapping_pins_is_refused_deletion_until_nothing_pins_it()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var author = await TokenAsync(client, "read", "author");

        // A kind of its own, so deleting it cannot pull a template from under any other test.
        var kind = $"test:wks:master-data--ApiThing{Guid.NewGuid():N}:1.0.0";
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"kind":{"type":"string"},"data":{"type":"object","properties":{"Name":{"type":"string"}}}}}""").RootElement.Clone();
        var saved = await ReadAsync<DeliveryTemplateSavedDto>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", new { kind, schema, origin = "test" }));
        Assert.Equal("created", saved.Outcome);
        var version = saved.Template.Version;

        var mappingId = Guid.NewGuid();
        await using (var db = CatalogDatabase.Create(cs))
        {
            db.DeliveryMappings.Add(new DeliveryMapping
            {
                Id = mappingId,
                RepoId = Guid.NewGuid(),
                Reference = "ApiThing@1.0.0",
                Name = "ApiThing",
                Version = "1.0.0",
                Kind = kind,
                TemplateVersion = version,
                RelativePath = "mappings/ApiThing@1.0.0.yaml",
                ContentHash = new string('0', 64),
                Yaml = "documentType: mapping",
                SummaryJson = "{}",
                Status = "valid",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var deleteUrl = $"/api/v1/delivery/templates?kind={Uri.EscapeDataString(kind)}&version={version}";
        try
        {
            using (var pinned = await SendAsync(client, author, HttpMethod.Delete, deleteUrl))
            {
                Assert.Equal(HttpStatusCode.Conflict, pinned.StatusCode);
                Assert.Contains("pinned by mapping(s) ApiThing@1.0.0", await pinned.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.DeliveryMappings.Where(m => m.Id == mappingId).ExecuteDeleteAsync();
        }

        using (var deleted = await SendAsync(client, author, HttpMethod.Delete, deleteUrl))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var gone = await SendAsync(client, author, HttpMethod.Get, DetailUrl(kind, version));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [SkippableFact]
    public async Task The_builder_drafts_from_the_repository_cache_and_checks_what_it_writes()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var author = await TokenAsync(client, "read", "operate", "author");
        foreach (var (kind, file) in new[] { (WellLogKind, WellLogFile), (WellboreKind, WellboreFile) })
        {
            using var response = await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", SaveBody(kind, file));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_tpl_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "welllog-tpl-" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var sourceId = Guid.NewGuid();
        await SeedRepositoryAsync(cs, repoName, repoId, sourceId, flowName, pipelineId);
        try
        {
            var repos = await ReadAsync<List<DeliveryBuilderRepoDto>>(await SendAsync(client, author, HttpMethod.Get, "/api/v1/delivery/mapping-builder/repos"));
            var repo = Assert.Single(repos, r => r.RepoId == repoId);
            Assert.Equal(sourceId, repo.SourceId);
            Assert.Equal(ReferenceVersion, repo.CacheVersion);
            var flow = Assert.Single(repo.Flows);
            Assert.Equal("WellLog@1.4.0", flow.Mapping);
            Assert.Equal("opendes", flow.Parameters["dataPartition"]);
            Assert.Equal(["FacilityName"], Assert.Single(repo.CacheTypes, c => c.Name == "Wellbore").Fields);

            var detail = await ReadAsync<DeliveryTemplateDetailDto>(await SendAsync(client, author, HttpMethod.Get, DetailUrl(WellLogKind, WellLogVersion) + "&repoId=" + repoId));
            Assert.Equal(["UnitOfMeasure"], Assert.Single(detail.Variables, v => v.Path == "osdu.data.Curves[].CurveUnit").CacheTypes);

            var draft = await ReadAsync<MappingDraft>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/draft", new
            {
                repoId, kind = WellLogKind, version = WellLogVersion, name = "WellLog", mappingVersion = "9.0.0", system = "recall",
            }));
            var prefilled = Assert.Single(draft.Entries, e => e.Target == "osdu.data.WellboreID");
            Assert.True(prefilled.Prefilled);
            Assert.Equal(MappingDraftInput.Cache, prefilled.Input);
            Assert.Equal("FacilityName", Assert.Single(prefilled.FindBy).Field);

            // The sample mapping, opened in the builder and written back, passes the check against the seeded cache.
            var sample = await File.ReadAllTextAsync(Path.Combine(SampleRoot, "mappings", "WellLog@1.4.0.yaml"));
            var parsed = await ReadAsync<DeliveryMappingParseResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/parse", new { yaml = sample, path = "mappings/WellLog@1.4.0.yaml" }));
            Assert.Empty(parsed.Issues);
            Assert.NotNull(parsed.Draft);
            var parameters = new Dictionary<string, string> { ["dataPartition"] = "opendes" };
            var checkedSample = await ReadAsync<DeliveryMappingComposeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/compose", new { repoId, draft = parsed.Draft, parameters }));
            Assert.True(checkedSample.Valid, string.Join(Environment.NewLine, checkedSample.Issues.Select(i => i.Message)));
            Assert.Contains("target: osdu.data.WellboreID", checkedSample.Yaml, StringComparison.Ordinal);

            // A wellbore reference read from the unit cache is written, and refused by the check against the template.
            var wrong = parsed.Draft with
            {
                Entries = parsed.Draft.Entries.Select(e => e.Target == "osdu.data.WellboreID"
                    ? e with { CacheType = "UnitOfMeasure", FindBy = [new MappingDraftFind("Code", "wellbore_uwi", null)] }
                    : e).ToList(),
            };
            var refused = await ReadAsync<DeliveryMappingComposeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/compose", new { repoId, draft = wrong, parameters }));
            Assert.False(refused.Valid);
            Assert.Contains(refused.Issues, i => i.Severity == "error" && i.Message.Contains("writes the id of a cached UnitOfMeasure", StringComparison.Ordinal));

            // Without a repository there is no cache to check against, and the check says so.
            var noCache = await ReadAsync<DeliveryMappingComposeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/compose", new { repoId = (Guid?)null, draft = parsed.Draft, parameters }));
            Assert.False(noCache.Valid);
            Assert.Contains(noCache.Issues, i => i.Message.Contains("reads cache.Wellbore, which reference snapshot 'none' does not hold", StringComparison.Ordinal));

            // Browsing OSDU is queued on a node through the flow's connection.
            var search = await ReadAsync<ComputeTaskAccepted>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates/search", new { pipelineId, entityType = "master-data--Wellbore" }));
            var fetch = await ReadAsync<ComputeTaskAccepted>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates/fetch", new { pipelineId, kind = WellboreKind }));
            await using var db = CatalogDatabase.Create(cs);
            var tasks = await db.ComputeTasks.AsNoTracking().Where(t => t.TaskId == search.TaskId || t.TaskId == fetch.TaskId).ToListAsync();
            Assert.Contains(tasks, t => t.TaskId == search.TaskId && t.Operation == "delivery-schema-search" && t.ArgumentsJson.Contains("master-data--Wellbore", StringComparison.Ordinal));
            Assert.Contains(tasks, t => t.TaskId == fetch.TaskId && t.Operation == "delivery-schema-fetch" && t.ArgumentsJson.Contains(WellboreKind, StringComparison.Ordinal));
        }
        finally
        {
            await CleanupAsync(cs, repoId, flowName);
        }
    }

    /// <summary>
    /// A repository as the sync would leave it: a tracked source, one delivery flow that renders with the sample WellLog
    /// mapping, the cache definitions of the sample metadata sync, and the sample reference snapshot's items as the current
    /// cache version.
    /// </summary>
    private static async Task SeedRepositoryAsync(string cs, string repoName, Guid repoId, Guid sourceId, string flowName, Guid pipelineId)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, RemoteUrl = "https://example/" + repoName + ".git", RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now });
        db.RepoSources.Add(new CatalogRepoSource { Id = sourceId, Name = repoName, RemoteUrl = "https://example/" + repoName + ".git", Branch = "main" });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = pipelineId,
            RepoId = repoId,
            Name = flowName,
            Kind = "delivery",
            RelativePath = "flows/" + flowName + ".yaml",
            ContentHash = new string('0', 64),
            Yaml = $"""
                flowType: delivery
                name: {flowName}
                source:
                  location: C:/drops/{flowName}
                render:
                  mapping: WellLog@1.4.0
                  parameters:
                    dataPartition: opendes
                target:
                  endpoint: https://osdu.example.test
                  headers:
                    data-partition-id: opendes
                  protocol: osduRecord
                """,
            DefinitionJson = $$"""{"name":"{{flowName}}","flowKind":"delivery"}""",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });

        var fields = new Dictionary<string, string>
        {
            ["UnitOfMeasure"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"},{"path":"data.ID","as":"ID"}]""",
            ["LogCurveBusinessValue"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"}]""",
            ["VerticalMeasurementType"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"}]""",
            ["Wellbore"] = """[{"path":"data.FacilityName","as":"FacilityName"}]""",
        };

        var snapshotId = Guid.NewGuid();
        db.DeliverySnapshots.Add(new DeliverySnapshot
        {
            Id = snapshotId,
            RepoId = repoId,
            Kind = "references",
            Name = ReferenceVersion,
            Version = ReferenceVersion,
            CapturedUtc = now,
            Current = true,
            RelativePath = "snapshots/references/" + ReferenceVersion + "/manifest.json",
            SummaryJson = "{}",
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });

        foreach (var (type, declared) in fields)
        {
            var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(SampleRoot, "snapshots", "references", ReferenceVersion, type + ".json")))!.AsObject();
            var entityType = file["entityType"]!.GetValue<string>();
            db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                FlowName = "osdu-cache-sync",
                RelativePath = "flows/osdu-cache-sync.yaml",
                Name = type,
                EntityType = entityType,
                Kind = "osdu:wks:" + entityType + ":*",
                FieldsJson = declared,
                MakeCurrent = true,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });

            foreach (var item in file["items"]!.AsArray().OfType<JsonObject>())
            {
                var values = new JsonObject();
                foreach (var (name, value) in item)
                {
                    if (name != "id")
                    {
                        values[name] = value?.DeepClone();
                    }
                }

                db.DeliverySnapshotItems.Add(new DeliverySnapshotItem
                {
                    SnapshotId = snapshotId,
                    RepoId = repoId,
                    TypeName = type,
                    EntityType = entityType,
                    RecordId = item["id"]!.GetValue<string>(),
                    FieldsJson = values.ToJsonString(),
                    Terms = string.Join('\n', values.Select(kv => kv.Value?.ToString() ?? string.Empty)),
                });
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task CleanupAsync(string cs, Guid repoId, string flowName)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.DeliverySnapshotItems.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
        await db.DeliverySnapshots.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
        await db.ComputeTasks.Where(t => t.SourceRef == flowName).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        var repoName = await db.Repos.Where(r => r.Id == repoId).Select(r => r.Name).FirstOrDefaultAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        if (repoName is not null)
        {
            await db.RepoSources.Where(s => s.Name == repoName).ExecuteDeleteAsync();
        }
    }

    private static object SaveBody(string kind, string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleRoot, "templates", file)));
        return new { kind, schema = document.RootElement.Clone(), origin = "file " + file };
    }

    private static string DetailUrl(string kind, string version) => $"/api/v1/delivery/templates/detail?kind={Uri.EscapeDataString(kind)}&version={version}";

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory().WithCatalog(cs).WithSetting("ControlPlane:Worker:Enabled", "false");

    private static async Task<string> TokenAsync(HttpClient client, params string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/token", UriKind.Relative), new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Web);
        }

        return await client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.StatusCode}: {text}");
            var value = JsonSerializer.Deserialize<T>(text, Web);
            Assert.NotNull(value);
            return value;
        }
    }
}
