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
/// A record's row through its ingestion table, as the API serves it. Traceability is the product (CLAUDE.md), and a
/// record's history does not start at OSDU: its row reached the delivery flow through a file a pre-ingestion flow landed
/// and an ingestion run that wrote it into the table. The history is of the row's changes, not of the runs: the ingestion
/// flow restamps a row only when it changes, so a file landed again and loaded again unchanged is no part of it. The runs
/// that made each change are named from the platform's own records, and only when they prove it.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryRecordChainApiTests
{
    [Fact]
    public async Task A_record_lists_the_changes_of_its_row_each_with_the_runs_that_made_it_and_not_the_runs_that_changed_nothing()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var marker = "CH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var flowName = $"{marker}-delivery";
        var flowId = FlowId.Of(flowName);
        var key = new DeliveryKey(Guid.NewGuid());
        var first = $"{marker}_welllog_1.csv";
        var second = $"{marker}_welllog_2.csv";
        var repoId = Guid.NewGuid();
        var prePipeline = Guid.NewGuid();
        var ingPipeline = Guid.NewGuid();
        var day = new DateTime(2026, 9, 4, 5, 0, 0, DateTimeKind.Utc);
        var loaded = day.AddMinutes(5).AddSeconds(30);
        var changed = day.AddHours(3).AddMinutes(5).AddSeconds(30);
        var deleted = day.AddHours(4).AddMinutes(5).AddSeconds(30);
        var runs = new List<Guid>();
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            // The record: delivered from the first file's row, then changed by the second file to what OSDU holds
            // (recorded, nothing sent), then marked deleted by the ingestion flow's key match.
            var submission = Guid.NewGuid();
            await ledger.UpsertPendingAsync(flowId,
                [Staged(flowId, key, $"wells:{marker}/L-1", first, loaded) with { PendingSourceRowNumber = 4, SourceInsertedUtc = loaded, LastSubmissionId = submission }]);
            var claimed = Assert.Single((await ledger.ClaimAsync(flowId, submission, "w", 10, TimeSpan.FromMinutes(5), day.AddHours(1))).Records);
            // Settled as a worker settles it: the completion appended under the lease that claimed it, then applied.
            await ledger.AppendAsync(flowId, claimed.LeaseOwner!, new LeaseAppend([], [new RecordCompletion
            {
                DeliveryKey = key,
                Status = RecordStatus.Delivered,
                Promote = true,
                TargetVersion = 1,
                Claimed = ClaimedWork.Of(claimed),
                Attempt = new AttemptRecord
                {
                    DeliveryKey = key, SubmissionId = submission, Worker = "w", StartedUtc = day.AddHours(1), CompletedUtc = day.AddHours(1),
                    Outcome = AttemptOutcome.Delivered, Phase = "metadata", TargetVersion = 1,
                    SourceFileName = first, SourceRowNumber = 4, SourceUpdatedUtc = loaded,
                },
            }]));
            await ledger.CheckpointLeaseAsync(claimed.LeaseOwner!, day.AddHours(1));
            await ledger.MarkSkippedAsync(flowId, [new SkippedRecord
            {
                DeliveryKey = key, Kind = SkipKind.Rendered, Reason = "metadata and payload hashes unchanged", SourceFingerprint = "fp-2",
                Origin = new RecordOrigin(second, 9, changed), SourceInsertedUtc = loaded,
            }], Guid.NewGuid());
            await ledger.MarkHeldAsync(flowId, [Staged(flowId, key, $"wells:{marker}/L-1", second, changed) with
            {
                PendingSourceRowNumber = 9, PendingSourceDeletedUtc = deleted, PendingDocumentRef = null, LastError = "the ingestion table marked the record row deleted",
            }]);
            await AddInterfaceAsync(cs, repoId, flowName, flowId, $"[{marker}Db].[arc].[WellLog]", day);

            // What ran, as the platform recorded it. The first file was landed and loaded once, which stamped the row,
            // then landed and loaded again twice with nothing changed. The second file changed the row, and a later
            // ingestion run's key match marked it deleted.
            await using (var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs)))
            {
                catalog.Pipelines.AddRange(
                    Pipeline(prePipeline, repoId, $"{marker}-pre", "file", wave: 1),
                    Pipeline(ingPipeline, repoId, $"{marker}-ing", "ing", wave: 2));

                CatalogRun Landing(DateTime at, string file)
                {
                    var run = Run(Guid.NewGuid(), prePipeline, repoId, $"{marker}-pre", "file", at.AddMinutes(1));
                    run.StartUtc = at;
                    catalog.RunFiles.Add(new CatalogRunFile { RunId = run.RunId, RepoId = repoId, Name = file, Path = $"/drop/{file}", Rows = 5, Columns = 19, SizeBytes = 4096 });
                    return run;
                }

                CatalogRun Loading(DateTime at)
                {
                    var run = Run(Guid.NewGuid(), ingPipeline, repoId, $"{marker}-ing", "ing", at.AddMinutes(1));
                    run.StartUtc = at;
                    run.EndUtc = at.AddMinutes(1);
                    run.RowsLoaded = 5;
                    return run;
                }

                var all = new[]
                {
                    Landing(day, first), Loading(day.AddMinutes(5)),
                    Landing(day.AddHours(1), first), Loading(day.AddHours(1).AddMinutes(5)),
                    Landing(day.AddHours(2), first), Loading(day.AddHours(2).AddMinutes(5)),
                    Landing(day.AddHours(3), second), Loading(day.AddHours(3).AddMinutes(5)),
                    Loading(day.AddHours(4).AddMinutes(5)),
                };
                catalog.Runs.AddRange(all);
                runs.AddRange(all.Select(r => r.RunId));
                catalog.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId,
                    Flow = $"{marker}-ing",
                    PipelineId = ingPipeline,
                    Relation = "Writes",
                    ObjectKey = $"${{env:{marker.ToLowerInvariant()}_db}}|{marker.ToLowerInvariant()}db|arc|welllog",
                    ObjectName = "WellLog",
                    Tier = "Declared",
                });
                await catalog.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var response = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{key.Value:D}/chain");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var chain = json.RootElement;
            Assert.Equal($"[{marker}Db].[arc].[WellLog]", chain.GetProperty("sourceTable").GetString());
            Assert.Equal(loaded, Utc(chain.GetProperty("insertedUtc")));
            Assert.False(chain.GetProperty("truncated").GetBoolean());

            // Three changes, newest first. Nine runs ran; the two that loaded the first file again changed nothing, and
            // the landings behind them carried nothing into a change, so none of the four is named.
            var changes = chain.GetProperty("changes").EnumerateArray().ToList();
            Assert.Equal(["deleted", "changed", "loaded"], changes.Select(c => c.GetProperty("kind").GetString()));
            Assert.Equal([deleted, changed, loaded], changes.Select(c => Utc(c.GetProperty("atUtc"))));
            var named = changes
                .SelectMany(c => new[] { c.GetProperty("loading"), c.GetProperty("landing") })
                .Where(r => r.ValueKind != JsonValueKind.Null)
                .Select(r => r.TryGetProperty("run", out var run) ? run.GetProperty("runId").GetGuid() : r.GetProperty("runId").GetGuid())
                .ToList();
            Assert.Equal([runs[8], runs[7], runs[6], runs[1], runs[0]], named);

            // The deletion: the ingestion run whose key match marked it, and no landing, since it processed no file.
            Assert.Equal(runs[8], changes[0].GetProperty("loading").GetProperty("runId").GetGuid());
            Assert.Equal(JsonValueKind.Null, changes[0].GetProperty("landing").ValueKind);

            // The change: the second file's row, the run that wrote it, and the landing that brought the file in.
            Assert.Equal((second, 9L), (changes[1].GetProperty("fileName").GetString(), changes[1].GetProperty("rowNumber").GetInt64()));
            Assert.Equal(runs[7], changes[1].GetProperty("loading").GetProperty("runId").GetGuid());
            Assert.Equal(runs[6], changes[1].GetProperty("landing").GetProperty("run").GetProperty("runId").GetGuid());

            // The arrival: the first landing of the first file and the run that inserted the row, not the file's later
            // landings, however many there were.
            var arrival = changes[2];
            Assert.Equal((first, 4L), (arrival.GetProperty("fileName").GetString(), arrival.GetProperty("rowNumber").GetInt64()));
            var loading = arrival.GetProperty("loading");
            Assert.Equal((runs[1], "ingestion", 2, 5L), (loading.GetProperty("runId").GetGuid(), loading.GetProperty("stage").GetString(), loading.GetProperty("wave").GetInt32(), loading.GetProperty("rows").GetInt64()));
            var landing = arrival.GetProperty("landing");
            Assert.Equal(runs[0], landing.GetProperty("run").GetProperty("runId").GetGuid());
            Assert.Equal(
                ("pre-ingestion", first, $"/drop/{first}", 4096L),
                (landing.GetProperty("run").GetProperty("stage").GetString(), landing.GetProperty("fileName").GetString(), landing.GetProperty("filePath").GetString(), landing.GetProperty("sizeBytes").GetInt64()));

            // A record the ledger does not hold has no chain at all.
            using var missing = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{Guid.NewGuid():D}/chain");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryInterfaces.Where(i => i.LedgerFlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
            await using var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs));
            await catalog.RunFiles.Where(f => f.Name.StartsWith(marker)).ExecuteDeleteAsync();
            await catalog.LineageEdges.Where(e => e.PipelineId == ingPipeline).ExecuteDeleteAsync();
            await catalog.Runs.Where(r => runs.Contains(r.RunId)).ExecuteDeleteAsync();
            await catalog.Pipelines.Where(p => p.Id == prePipeline || p.Id == ingPipeline).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// What the ledger cannot know is said, never filled in. A row that changed before any plan read it arrived at its
    /// insert moment from a file the ledger never saw; a record whose table carries no insert column has an earliest
    /// version, not an arrival; with no table the sync recorded, no run is named and a note says why; and a record whose
    /// row was never read with its stamp has no changes, and a note.
    /// </summary>
    [Fact]
    public async Task What_the_ledger_does_not_hold_is_named_as_missing_rather_than_guessed()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var marker = "CT" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var flowId = FlowId.Of($"{marker}-delivery");
        var late = new DeliveryKey(Guid.NewGuid());
        var unknown = new DeliveryKey(Guid.NewGuid());
        var unread = new DeliveryKey(Guid.NewGuid());
        var arrived = new DateTime(2026, 9, 5, 4, 2, 0, DateTimeKind.Utc);
        var stamped = arrived.AddDays(1);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));

        try
        {
            await ledger.UpsertPendingAsync(flowId,
            [
                Staged(flowId, late, $"wells:{marker}/LATE", $"{marker}_welllog.csv", stamped) with { SourceInsertedUtc = arrived },
                Staged(flowId, unknown, $"wells:{marker}/UNKNOWN", $"{marker}_welllog.csv", stamped),
                Staged(flowId, unread, $"wells:{marker}/UNREAD", $"{marker}_welllog.csv", stamped) with { PendingSourceUpdatedUtc = null },
            ]);

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The row changed before any plan read it: its arrival is the insert moment, from no file the ledger holds,
            // and no sync names the table, so no run is named and the note says why.
            using var lateResponse = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{late.Value:D}/chain");
            using var lateJson = JsonDocument.Parse(await lateResponse.Content.ReadAsStringAsync());
            var changes = lateJson.RootElement.GetProperty("changes").EnumerateArray().ToList();
            Assert.Equal(["changed", "loaded"], changes.Select(c => c.GetProperty("kind").GetString()));
            Assert.Equal(arrived, Utc(changes[1].GetProperty("atUtc")));
            Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("fileName").ValueKind);
            Assert.All(changes, c => Assert.Equal(JsonValueKind.Null, c.GetProperty("loading").ValueKind));
            Assert.All(changes, c => Assert.Equal(JsonValueKind.Null, c.GetProperty("landing").ValueKind));
            Assert.Contains("not recorded by the repository sync", lateJson.RootElement.GetProperty("note").GetString()!, StringComparison.Ordinal);

            // No insert moment: the only version is the earliest one, not an arrival.
            using var unknownResponse = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{unknown.Value:D}/chain");
            using var unknownJson = JsonDocument.Parse(await unknownResponse.Content.ReadAsStringAsync());
            Assert.Equal("earliest", Assert.Single(unknownJson.RootElement.GetProperty("changes").EnumerateArray().ToList()).GetProperty("kind").GetString());
            Assert.Equal(JsonValueKind.Null, unknownJson.RootElement.GetProperty("insertedUtc").ValueKind);

            // Never read with its stamp: no changes, and why.
            using var unreadResponse = await GetAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{unread.Value:D}/chain");
            using var unreadJson = JsonDocument.Parse(await unreadResponse.Content.ReadAsStringAsync());
            Assert.Empty(unreadJson.RootElement.GetProperty("changes").EnumerateArray());
            Assert.Contains("holds no version of this record's row", unreadJson.RootElement.GetProperty("note").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
        }
    }

    /// <summary>A moment the API wrote, as UTC: a stored moment is UTC by contract, whether or not it carries its zone.</summary>
    private static DateTime Utc(JsonElement value) => DateTime.SpecifyKind(DateTime.Parse(value.GetString()!.TrimEnd('Z'), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    /// <summary>What the sync records for a flow: the ledger it writes, and the record table it reads.</summary>
    private static async Task AddInterfaceAsync(string cs, Guid repoId, string flowName, Guid flowId, string table, DateTime seen)
    {
        await using var osdu = SampleEstate.Context(cs);
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
            FirstSeenUtc = seen,
            LastSeenUtc = seen,
            Active = true,
        });
        await osdu.SaveChangesAsync();
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
