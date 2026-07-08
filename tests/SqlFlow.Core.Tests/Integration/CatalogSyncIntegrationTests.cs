using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end shadow-catalog sync against the real sink: <see cref="CatalogDatabase.MigrateAsync"/> provisions the
/// EF-managed <c>catalog</c> schema in the database, then a sync projects a temp YAML estate and its on-disk
/// run.json into it. Asserts the pipeline (with its queryable definition JSON) and the run land, that re-syncing
/// is idempotent (no duplicate runs), and that removing a flow from the estate deactivates its pipeline while its
/// run history survives. Cleans up its own repo's rows so repeated runs stay isolated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_catsync_" + Guid.NewGuid().ToString("N"));

    public CatalogSyncIntegrationTests() => Directory.CreateDirectory(_dir);

    private void WriteFlow(string flowName, string relativePath)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            name: __NAME__
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: CatOrders
            """.Replace("__NAME__", flowName, StringComparison.Ordinal));
    }

    private void WriteRun(string flowName, Guid runId, bool success)
    {
        var dir = Path.Combine(_dir, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName), $"20260617-100000_{runId.ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.json"), $$"""
            {
              "schemaVersion": 1,
              "flowKind": "file",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": {{(success ? "true" : "false")}},
              "writtenUtc": "2026-06-17T10:00:00Z",
              "result": {
                "rowsLoaded": 5, "totalMs": 100.0,
                "processedFiles": [{ "name": "data.csv", "path": "./data.csv", "rows": 5, "columns": 2, "sizeBytes": 50 }],
                "assertions": [{ "name": "nonEmpty", "result": "5", "assertedValue": "5", "evaluated": true }]
              }
            }
            """);
    }

    [SkippableFact]
    public async Task Migrate_IsIdempotent_AndLeavesNoPending()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await CatalogDatabase.MigrateAsync(cs); // again: a no-op, never an error

        var (applied, pending) = await CatalogDatabase.StatusAsync(cs);
        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    [SkippableFact]
    public async Task Sync_ProjectsPipelineAndRun_IsIdempotent_AndDeactivatesRemoved()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_test_" + suffix;
        var flowName = "cat_orders_" + suffix;
        var runId = Guid.NewGuid();
        var repoId = FlowIdentity.FromName(repo);

        WriteFlow(flowName, "flows/orders.flow.yaml");
        WriteRun(flowName, runId, success: true);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var first = await new CatalogSync().SyncAsync(db, _dir, repo, "https://example/repo.git", DateTime.UtcNow);
                Assert.Equal(1, first.PipelinesAdded);
                Assert.Equal(1, first.RunsAdded);
                Assert.Equal(1, first.RunFilesAdded);       // the run.json processedFiles
                Assert.Equal(1, first.RunAssertionsAdded);  // the run.json assertions
                Assert.True(first.LineageEdges >= 1);       // the flow writes its target table -> at least one edge
                Assert.True(first.ObjectsUpserted >= 1);
                Assert.False(first.LineageConnected);       // offline (no --connect)
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var pipeline = await db.Pipelines.SingleAsync(p => p.Name == flowName);
                Assert.Equal(CatalogIdentity.Pipeline(repoId, flowName), pipeline.Id);
                Assert.Equal(repoId, pipeline.RepoId);
                Assert.Equal("file", pipeline.Kind);
                Assert.True(pipeline.Active);
                Assert.Contains(flowName, pipeline.DefinitionJson, StringComparison.Ordinal); // the YAML mapped into queryable JSON
                Assert.False(string.IsNullOrEmpty(pipeline.Yaml));

                var run = await db.Runs.SingleAsync(r => r.RunId == runId);
                Assert.Equal(CatalogIdentity.Pipeline(repoId, flowName), run.PipelineId); // joins to the pipeline
                Assert.Equal(repoId, run.RepoId);
                Assert.Equal(5, run.RowsLoaded);

                // Run drill-down detail landed and joins back to the run.
                Assert.True(await db.RunFiles.AnyAsync(f => f.RunId == runId && f.Name == "data.csv"));
                Assert.True(await db.RunAssertions.AnyAsync(a => a.RunId == runId && a.Name == "nonEmpty"));

                // Lineage edges are repo-scoped and join to the pipeline; objects are queryable by name across repos.
                Assert.True(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId));
                Assert.True(await db.Objects.AnyAsync());

                Assert.True(await db.Repos.AnyAsync(r => r.Id == repoId && r.Name == repo));
            }

            // Re-sync: unchanged flow + already-known run -> nothing added.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var again = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(0, again.PipelinesAdded);
                Assert.Equal(0, again.RunsAdded);
                Assert.Equal(1, again.RunsSkipped);
            }

            // The flow leaves the estate -> its pipeline is deactivated, its run history is kept.
            File.Delete(Path.Combine(_dir, "flows", "orders.flow.yaml"));
            await using (var db = CatalogDatabase.Create(cs))
            {
                var removed = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(1, removed.PipelinesDeactivated);
                Assert.False((await db.Pipelines.SingleAsync(p => p.Name == flowName)).Active);
                Assert.True(await db.Runs.AnyAsync(r => r.RunId == runId));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_HonorsExcludedFlowSelection_UnderNoTracking_DeactivatesAndReactivates()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_sel_" + suffix;
        var flowA = "cat_sel_a_" + suffix;
        var flowB = "cat_sel_b_" + suffix;
        var repoId = FlowIdentity.FromName(repo);

        WriteFlow(flowA, "flows/a.flow.yaml");
        WriteFlow(flowB, "flows/b.flow.yaml");
        await CatalogDatabase.MigrateAsync(cs);

        // The control plane pools its catalog context with QueryTrackingBehavior.NoTracking; the sync's
        // update/deactivate mutations MUST still persist. Running the whole lifecycle through a NoTracking context
        // is the regression guard for that fix (without it, the deactivation below silently no-ops).
        static CatalogDbContext NoTracking(string connectionString)
        {
            var db = CatalogDatabase.Create(connectionString);
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            return db;
        }

        var excludeB = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "flows/b.flow.yaml" };

        try
        {
            // 1. Import with flow B excluded: only A becomes a pipeline; B is never projected.
            await using (var db = NoTracking(cs))
            {
                var r = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, excludedFlowPaths: excludeB);
                Assert.Equal(1, r.PipelinesAdded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await db.Pipelines.AnyAsync(p => p.Name == flowA && p.Active));
                Assert.False(await db.Pipelines.AnyAsync(p => p.Name == flowB));
            }

            // 2. Re-include B (no exclusion): it appears and is active.
            await using (var db = NoTracking(cs))
            {
                var r = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, excludedFlowPaths: null);
                Assert.Equal(1, r.PipelinesAdded); // B added; A unchanged
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await db.Pipelines.AnyAsync(p => p.Name == flowB && p.Active));
            }

            // 3. Exclude B again: the previously-imported pipeline is DEACTIVATED (the mutation persists under
            // NoTracking), and its history would survive. A stays active.
            await using (var db = NoTracking(cs))
            {
                var r = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, excludedFlowPaths: excludeB);
                Assert.Equal(1, r.PipelinesDeactivated);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.True(await db.Pipelines.AnyAsync(p => p.Name == flowA && p.Active));
                Assert.False((await db.Pipelines.SingleAsync(p => p.Name == flowB)).Active);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_HealsTheDatabaselessTwin_WhenTheIdentityGainsItsDatabase()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_heal_" + suffix;
        var flowName = "cat_heal_orders_" + suffix;
        var table = "HealOrders_" + suffix;
        var variable = "SQLFLOW_TEST_SINK_" + suffix.ToUpperInvariant();
        var repoId = FlowIdentity.FromName(repo);
        var serverRef = "${env:" + variable + "}";
        var weakKey = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, null, "dbo", table);
        var strongKey = SqlFlow.Lineage.Collection.NodeKey.For(serverRef, "SinkDb", "dbo", table);

        var flowPath = Path.Combine(_dir, "flows", "heal.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(flowPath)!);
        File.WriteAllText(flowPath, $$"""
            name: {{flowName}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:{{variable}}}
              schema: dbo
              table: {{table}}
            """);

        await CatalogDatabase.MigrateAsync(cs);
        try
        {
            // First sync with the reference unresolvable: the object lands under the database-less key,
            // exactly the pre-resolution catalog state this healing exists for.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.True(await db.Objects.AnyAsync(o => o.Key == weakKey));
            }

            // The reference becomes resolvable: the identity gains its database, and the weak twin
            // (no longer referenced by any repo's edges) is superseded and removed. The flow document is also
            // touched (a comment changes its content hash), because the sync recomputes lineage only when a
            // lineage input it can observe changed: pipeline content, the flow set, or new run artifacts. An
            // environment-only change rides along with the next content change or connected sync.
            Environment.SetEnvironmentVariable(variable, "Server=localhost;Initial Catalog=SinkDb;Integrated Security=true;");
            File.AppendAllText(flowPath, $"{Environment.NewLine}# sink reference resolvable since this revision{Environment.NewLine}");
            await using (var db = CatalogDatabase.Create(cs))
            {
                var healed = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(1, healed.ObjectsSuperseded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.Objects.AnyAsync(o => o.Key == weakKey));
                var strong = await db.Objects.SingleAsync(o => o.Key == strongKey);
                Assert.Equal("sinkdb", strong.Database);
                Assert.True(await db.LineageEdges.AnyAsync(e => e.RepoId == repoId && e.ObjectKey == strongKey));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == weakKey || o.Key == strongKey).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Sync_MirrorsYamlSchedule_PreservesApiPause_AndRemovesItWhenItLeavesGit()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_sch_" + suffix;
        var flowName = "cat_sch_orders_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var scheduleId = CatalogIdentity.YamlSchedule(repoId, flowName);
        var flowPath = Path.Combine(_dir, "flows", "orders.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(flowPath)!);

        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // A flow declaring a cron schedule in git.
            File.WriteAllText(flowPath, $$"""
                name: {{flowName}}
                schedule:
                  cron: "0 6 * * *"
                  timezone: "UTC"
                source:
                  type: csv
                  location: ./data.csv
                target:
                  connection: ${env:SQLFlowSinkConStr}
                  schema: dbo
                  table: CatOrders
                """);

            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var schedule = await db.Schedules.SingleAsync(s => s.Id == scheduleId);
                Assert.Equal("yaml", schedule.Source);
                Assert.Equal("0 6 * * *", schedule.Cron);
                Assert.Equal("UTC", schedule.Timezone);
                Assert.Equal(pipelineId, schedule.PipelineId);
                Assert.True(schedule.Enabled);
                Assert.NotNull(schedule.NextFireUtc);

                // An operator pauses the git schedule through the API.
                await ScheduleStore.SetPausedAsync(db, scheduleId, paused: true, nextFireUtcOnResume: null, DateTime.UtcNow);
            }

            // Re-sync the unchanged estate: the pause set through the API must survive.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.True((await db.Schedules.SingleAsync(s => s.Id == scheduleId)).Paused);
            }

            // The schedule leaves git (the flow stays, the schedule block is removed): the yaml schedule is removed.
            File.WriteAllText(flowPath, $$"""
                name: {{flowName}}
                source:
                  type: csv
                  location: ./data.csv
                target:
                  connection: ${env:SQLFlowSinkConStr}
                  schema: dbo
                  table: CatOrders
                """);
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.False(await db.Schedules.AnyAsync(s => s.Id == scheduleId));
                // The pipeline itself stays (only its schedule left git).
                Assert.True(await db.Pipelines.AnyAsync(p => p.Id == pipelineId && p.Active));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private void WriteIngFlow(string flowName, string relativePath, string sourceObject, string targetObject)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            flowType: ing
            name: __NAME__
            connections:
              dwh: ${env:SqlFlowSinkConStr}
            source:
              server: dwh
              object: __SRC__
            target:
              server: dwh
              object: __TRG__
            load:
              keyColumns: [Id]
            """
            .Replace("__NAME__", flowName, StringComparison.Ordinal)
            .Replace("__SRC__", sourceObject, StringComparison.Ordinal)
            .Replace("__TRG__", targetObject, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Sync_Lineage_OrdersFlowsIntoWaves_AndRecordsDependencies()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_wave_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var flowA = "wave_a_" + suffix;
        var flowB = "wave_b_" + suffix;

        // B reads the table A writes, so B must run in a later wave than A (declared lineage, offline).
        WriteIngFlow(flowA, "flows/a.flow.yaml", $"TestDB.dbo.Raw_{suffix}", $"TestDB.dbo.Staging_{suffix}");
        WriteIngFlow(flowB, "flows/b.flow.yaml", $"TestDB.dbo.Staging_{suffix}", $"TestDB.dbo.Final_{suffix}");
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(2, result.PipelinesAdded);
                Assert.True(result.Waves >= 2, $"expected >=2 waves, got {result.Waves}");
                Assert.True(result.FlowDependencies >= 1, $"expected >=1 dependency, got {result.FlowDependencies}");
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var a = await db.Pipelines.SingleAsync(p => p.Name == flowA);
                var b = await db.Pipelines.SingleAsync(p => p.Name == flowB);
                Assert.True(a.Wave >= 0);
                Assert.True(b.Wave > a.Wave, $"expected B (wave {b.Wave}) to run after A (wave {a.Wave})");
                Assert.True(await db.FlowDependencies.AnyAsync(d => d.RepoId == repoId && d.FromPipelineId == a.Id && d.ToPipelineId == b.Id));

                // The stamped object levels mirror the movement chain: the raw source (nothing produces it) is
                // level 0, the staging table A writes is level 1, and the final table B writes is level 2.
                var levelByName = await db.Objects
                    .Where(o => o.Name == $"Raw_{suffix}" || o.Name == $"Staging_{suffix}" || o.Name == $"Final_{suffix}")
                    .ToDictionaryAsync(o => o.Name, o => o.Level);
                Assert.Equal((int?)0, levelByName[$"Raw_{suffix}"]);
                Assert.Equal((int?)1, levelByName[$"Staging_{suffix}"]);
                Assert.Equal((int?)2, levelByName[$"Final_{suffix}"]);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task RecordRun_RecordsRunAndPipeline_WithoutFullSync_AndIsIdempotent()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_rec_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        var flowName = "rec_orders_" + suffix;
        var runId = Guid.NewGuid();
        var flowFile = Path.Combine(_dir, "flows", "orders.flow.yaml");

        WriteFlow(flowName, "flows/orders.flow.yaml");
        // A run.json carrying a host, written exactly where the engine would put it.
        var runDir = Path.Combine(_dir, ".sqlflow", "runs", RunHistoryWriter.SafeName(flowName), $"20260617-100000_{runId.ToString("N")[..8]}");
        Directory.CreateDirectory(runDir);
        var runJson = Path.Combine(runDir, "run.json");
        File.WriteAllText(runJson, $$"""
            {
              "schemaVersion": 1, "flowKind": "file", "flowName": "{{flowName}}", "runId": "{{runId}}",
              "success": true, "writtenUtc": "2026-06-17T10:00:00Z", "host": "node-test",
              "result": { "rowsLoaded": 5, "totalMs": 100.0,
                "processedFiles": [{ "name": "data.csv", "path": "./data.csv", "rows": 5, "columns": 2, "sizeBytes": 50 }],
                "assertions": [{ "name": "nonEmpty", "result": "5", "assertedValue": "5", "evaluated": true }] }
            }
            """);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // No full SyncAsync: the per-run write-back alone records the run AND ensures the pipeline row.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().RecordRunAsync(db, flowFile, runJson, repo, null, DateTime.UtcNow);
                Assert.Equal(PipelineChange.Added, result.PipelineChange);
                Assert.True(result.RunRecorded);
                Assert.Equal(1, result.RunFilesAdded);
                Assert.Equal(1, result.RunAssertionsAdded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var pipeline = await db.Pipelines.SingleAsync(p => p.Name == flowName);
                Assert.True(pipeline.Active);
                Assert.Equal(-1, pipeline.Wave); // the write-back does NOT compute lineage; wave stays "not computed"

                var run = await db.Runs.SingleAsync(r => r.RunId == runId);
                Assert.Equal("node-test", run.Host); // per-node attribution from the run.json header
                Assert.Equal(CatalogIdentity.Pipeline(repoId, flowName), run.PipelineId);
                Assert.True(await db.RunFiles.AnyAsync(f => f.RunId == runId));
                Assert.True(await db.RunAssertions.AnyAsync(a => a.RunId == runId));
            }

            // Recording the same run again is a no-op (a run is immutable; the unchanged flow only re-affirms).
            await using (var db = CatalogDatabase.Create(cs))
            {
                var again = await new CatalogSync().RecordRunAsync(db, flowFile, runJson, repo, null, DateTime.UtcNow);
                Assert.False(again.RunRecorded);
                Assert.Equal(PipelineChange.Unchanged, again.PipelineChange);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private void WriteFlowWithConnection(string flowName, string relativePath, string connection)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            name: __NAME__
            source:
              type: csv
              location: ./data.csv
            target:
              connection: __CONN__
              schema: dbo
              table: CatOrders
            """.Replace("__NAME__", flowName, StringComparison.Ordinal).Replace("__CONN__", connection, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Sync_RedactsEmbeddedSecret_NeverRestsInCatalog()
    {
        var cs = IntegrationDb.Require();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "cat_sec_" + suffix;
        var flowName = "cat_secret_" + suffix;
        var repoId = FlowIdentity.FromName(repo);

        // A flow that, against policy, inlines a credential. The sync must redact it (and warn), never store it.
        WriteFlowWithConnection(flowName, "flows/secret.flow.yaml", "Server=db;Database=x;User ID=u;Password=topsecret123;TrustServerCertificate=True");
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Contains(result.Warnings, w => w.Contains("credential", StringComparison.OrdinalIgnoreCase));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var pipeline = await db.Pipelines.SingleAsync(p => p.Name == flowName);
                Assert.DoesNotContain("topsecret123", pipeline.Yaml, StringComparison.Ordinal);
                Assert.DoesNotContain("topsecret123", pipeline.DefinitionJson, StringComparison.Ordinal);
                Assert.Contains("[redacted]", pipeline.Yaml, StringComparison.Ordinal);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
