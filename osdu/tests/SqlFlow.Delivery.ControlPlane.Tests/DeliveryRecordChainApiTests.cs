using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Where a record is in the whole chain, as the API serves it. Traceability is the product (CLAUDE.md), and the chain
/// an operator asks about does not start at OSDU: a row reaches a delivery flow through the file that pre-ingestion
/// landed and the run that loaded it into the ingestion tables. Those runs are the platform's own, recorded against the
/// files they processed, so the chain names what actually ran rather than reconstructing it.
/// </summary>
public sealed class DeliveryRecordChainApiTests
{
    private const string FlowName = "wells-welllog-03-header-delivery";

    [Fact]
    public async Task A_record_names_the_runs_that_carried_its_file_through_pre_ingestion_and_ingestion()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var key = new DeliveryKey(Guid.NewGuid());
        var marker = "CH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var file = $"{marker}_welllog.csv";
        var repoId = Guid.NewGuid();
        var prePipeline = Guid.NewGuid();
        var ingPipeline = Guid.NewGuid();
        var preRun = Guid.NewGuid();
        var ingRun = Guid.NewGuid();
        var landed = new DateTime(2026, 9, 4, 5, 0, 0, DateTimeKind.Utc);
        var loaded = landed.AddMinutes(10);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                new RecordState
                {
                    DeliveryKey = key,
                    FlowId = flowId,
                    SourceKey = $"wells:{marker}/L-1",
                    Label = $"{marker} wellbore / STAT_COMP",
                    MappingName = SampleEstate.WellboreMapping,
                    Status = RecordStatus.Pending,
                    SourceKeyJson = $$"""["{{marker}}","L-1"]""",
                    PendingSourceFileName = file,
                    PendingSourceRowNumber = 4,
                    PendingSourceUpdatedUtc = loaded,
                    PendingDocumentRef = "1:0:10",
                    PendingMetadata = true,
                },
            ]);

            // The two runs that carried the file, as the platform records them: a file flow that landed it, and the
            // ingestion flow that loaded it into the table the delivery flow reads.
            await using (var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs)))
            {
                catalog.Pipelines.AddRange(
                    Pipeline(prePipeline, repoId, $"{marker}-pre", "file", wave: 1),
                    Pipeline(ingPipeline, repoId, $"{marker}-ing", "ing", wave: 2));
                catalog.Runs.AddRange(
                    Run(preRun, prePipeline, repoId, $"{marker}-pre", "file", landed),
                    Run(ingRun, ingPipeline, repoId, $"{marker}-ing", "ing", loaded));
                catalog.RunFiles.AddRange(
                    new CatalogRunFile { RunId = preRun, RepoId = repoId, Name = file, Path = $"/drop/{file}", Rows = 120, Columns = 19, SizeBytes = 4096 },
                    new CatalogRunFile { RunId = ingRun, RepoId = repoId, Name = file, Path = $"/drop/{file}", Rows = 120, Columns = 19, SizeBytes = 4096 });
                await catalog.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var response = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{key.Value:D}/chain");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var chain = json.RootElement;

            Assert.True(chain.GetProperty("fileKnown").GetBoolean());
            Assert.Equal(file, chain.GetProperty("fileName").GetString());
            Assert.Equal(4, chain.GetProperty("rowNumber").GetInt64());

            // Newest first, each stage named in the estate's vocabulary, with the run and flow to open.
            var stages = chain.GetProperty("stages").EnumerateArray().ToList();
            Assert.Equal(2, stages.Count);
            Assert.Equal("ingestion", stages[0].GetProperty("stage").GetString());
            Assert.Equal(ingRun, stages[0].GetProperty("runId").GetGuid());
            Assert.Equal(ingPipeline, stages[0].GetProperty("pipelineId").GetGuid());
            Assert.Equal(2, stages[0].GetProperty("wave").GetInt32());
            Assert.Equal(120, stages[0].GetProperty("rows").GetInt64());
            Assert.True(stages[0].GetProperty("success").GetBoolean());
            Assert.Equal("pre-ingestion", stages[1].GetProperty("stage").GetString());
            Assert.Equal(preRun, stages[1].GetProperty("runId").GetGuid());

            // A record whose file no run recorded gets an empty chain that says why, rather than a wrong one.
            var orphan = new DeliveryKey(Guid.NewGuid());
            await ledger.UpsertPendingAsync(flowId,
            [
                new RecordState
                {
                    DeliveryKey = orphan,
                    FlowId = flowId,
                    SourceKey = $"wells:{marker}/L-2",
                    MappingName = SampleEstate.WellboreMapping,
                    Status = RecordStatus.Pending,
                    PendingSourceFileName = $"{marker}_never_processed.csv",
                    PendingDocumentRef = "1:0:10",
                    PendingMetadata = true,
                },
            ]);
            using var unknown = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{orphan.Value:D}/chain");
            using var unknownJson = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
            Assert.False(unknownJson.RootElement.GetProperty("fileKnown").GetBoolean());
            Assert.Empty(unknownJson.RootElement.GetProperty("stages").EnumerateArray());
            Assert.Contains("No run in the catalog", unknownJson.RootElement.GetProperty("note").GetString()!, StringComparison.Ordinal);

            // A record the ledger does not hold has no chain at all.
            using var missing = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{Guid.NewGuid():D}/chain");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId && r.SourceKey.StartsWith($"wells:{marker}/")).ExecuteDeleteAsync();
            await using var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs));
            await catalog.RunFiles.Where(f => f.Name.StartsWith(marker)).ExecuteDeleteAsync();
            await catalog.Runs.Where(r => r.RunId == preRun || r.RunId == ingRun).ExecuteDeleteAsync();
            await catalog.Pipelines.Where(p => p.Id == prePipeline || p.Id == ingPipeline).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// An ingestion flow reads the table a pre-ingestion flow landed, not a file, so it records no file and a file name
    /// can never name it. It is found by what it was writing instead: the flows that write the record's own ingestion
    /// table are known from the lineage, and the run of one of them that was executing when the row was stamped is the
    /// run that loaded it. A row stamped when no such run was executing leaves the stage out rather than naming the
    /// table's latest load.
    /// </summary>
    [Fact]
    public async Task The_ingestion_run_is_the_one_that_was_writing_the_record_table_when_the_row_was_stamped()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var marker = "CT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var flowName = $"{marker}-delivery";
        var flowId = FlowId.Of(flowName);
        var inside = new DeliveryKey(Guid.NewGuid());
        var outside = new DeliveryKey(Guid.NewGuid());
        var repoId = Guid.NewGuid();
        var ingPipeline = Guid.NewGuid();
        var ingRun = Guid.NewGuid();
        var started = new DateTime(2026, 9, 5, 4, 0, 0, DateTimeKind.Utc);
        var ended = started.AddMinutes(4);
        var table = $"[{marker}Db].[ing].[WellLog]";
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                Staged(flowId, inside, $"wells:{marker}/IN", $"{marker}_welllog.csv", started.AddMinutes(2)),
                Staged(flowId, outside, $"wells:{marker}/OUT", $"{marker}_welllog.csv", ended.AddHours(3)),
            ]);

            await using (var osdu = SampleEstate.Context(cs))
            {
                // What the sync records for the flow: the ledger it writes, and the record table it reads.
                osdu.DeliveryInterfaces.Add(new DeliveryInterface
                {
                    Id = Guid.NewGuid(),
                    RepoId = repoId,
                    FlowName = flowName,
                    LedgerFlowId = flowId,
                    LedgerName = flowName,
                    Route = "storage",
                    MappingReference = SampleEstate.WellboreMapping,
                    RecordObject = table,
                    RelativePath = $"flows/{flowName}.yaml",
                    FirstSeenUtc = started,
                    LastSeenUtc = started,
                    Active = true,
                });
                await osdu.SaveChangesAsync();
            }

            await using (var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs)))
            {
                catalog.Pipelines.Add(Pipeline(ingPipeline, repoId, $"{marker}-ing", "ing", wave: 2));
                var run = Run(ingRun, ingPipeline, repoId, $"{marker}-ing", "ing", ended);
                run.StartUtc = started;
                run.EndUtc = ended;
                run.RowsLoaded = 42;
                catalog.Runs.Add(run);

                // The lineage every flow kind declares: this ingestion flow writes that table.
                catalog.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId,
                    Flow = $"{marker}-ing",
                    PipelineId = ingPipeline,
                    Relation = "Writes",
                    ObjectKey = $"${{env:{marker.ToLowerInvariant()}_db}}|{marker.ToLowerInvariant()}db|ing|welllog",
                    ObjectName = "WellLog",
                    Tier = "Declared",
                });
                await catalog.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The row stamped while that run was executing: the run is named, as the run that loaded it, with the
            // table it was writing rather than a file it never processed.
            using var response = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{inside.Value:D}/chain");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var stage = Assert.Single(json.RootElement.GetProperty("stages").EnumerateArray().ToList());
            Assert.Equal("ingestion", stage.GetProperty("stage").GetString());
            Assert.Equal(ingRun, stage.GetProperty("runId").GetGuid());
            Assert.Equal("table", stage.GetProperty("matchedBy").GetString());
            Assert.Equal("WellLog", stage.GetProperty("objectName").GetString());
            Assert.Equal("", stage.GetProperty("fileName").GetString());
            Assert.Equal(42, stage.GetProperty("rows").GetInt64());

            // A row stamped hours after every recorded run has no ingestion stage: naming the table's last load would
            // be a guess, and the note says what is missing instead.
            using var later = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{outside.Value:D}/chain");
            using var laterJson = JsonDocument.Parse(await later.Content.ReadAsStringAsync());
            Assert.Empty(laterJson.RootElement.GetProperty("stages").EnumerateArray());
            Assert.Contains("No run in the catalog", laterJson.RootElement.GetProperty("note").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryInterfaces.Where(i => i.LedgerFlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
            await using var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs));
            await catalog.LineageEdges.Where(e => e.PipelineId == ingPipeline).ExecuteDeleteAsync();
            await catalog.Runs.Where(r => r.RunId == ingRun).ExecuteDeleteAsync();
            await catalog.Pipelines.Where(p => p.Id == ingPipeline).ExecuteDeleteAsync();
        }
    }

    /// <summary>A pending record of one file, stamped by its ingestion table at the given moment.</summary>
    private static RecordState Staged(Guid flowId, DeliveryKey key, string sourceKey, string file, DateTime updatedUtc) => new()
    {
        DeliveryKey = key,
        FlowId = flowId,
        SourceKey = sourceKey,
        MappingName = SampleEstate.WellboreMapping,
        Status = RecordStatus.Pending,
        PendingSourceFileName = file,
        PendingSourceRowNumber = 1,
        PendingSourceUpdatedUtc = updatedUtc,
        PendingDocumentRef = "1:0:10",
        PendingMetadata = true,
    };

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, int wave) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = kind,
        Wave = wave,
        RelativePath = $"flows/{name}.yaml",
        Active = true,
    };

    private static CatalogRun Run(Guid runId, Guid pipelineId, Guid repoId, string name, string kind, DateTime at) => new()
    {
        RunId = runId,
        PipelineId = pipelineId,
        RepoId = repoId,
        FlowName = name,
        FlowKind = kind,
        Status = RunStatuses.Succeeded,
        Success = true,
        WrittenUtc = at,
        DurationSeconds = 12.5,
    };

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

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

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
