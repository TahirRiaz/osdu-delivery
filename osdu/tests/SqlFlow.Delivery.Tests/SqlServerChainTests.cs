using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using SqlFlow.Execution;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;
using Xunit.Abstractions;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The whole chain on a real SQL Server (docs/stage4-design.md section 6): files land through SQLFlow's pre-ingestion
/// flows, its ingestion flows key them into the tables the OSDU flow reads, and the delivery plans and sends them to a
/// fake target. Every flow runs through the platform's own document executor, in the wave order a run group puts them
/// in, so what these tests exercise is the path a scheduled chain takes rather than a test-only arrangement.
/// <para>Each test owns a fixture of its own (its schemas, its flow names, its ledger rows), so the suite is safe beside
/// the other SQL Server suites and safe run in parallel with itself.</para>
/// </summary>
public class SqlServerChainTests
{
    private readonly ITestOutputHelper _output;

    public SqlServerChainTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>The file the first batch of record rows lands from, which every record's origin then names.</summary>
    private const string LogFile = "welllog_20260901.csv";

    private const string CurveFile = "welllog_curves_20260901.csv";

    /// <summary>Case 1: the first run of the chain, from files to delivered records.</summary>
    [SkippableFact]
    public async Task A_first_run_loads_the_tables_renders_what_the_mapping_fixtures_pin_and_delivers_every_log()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);

        // The chain runs in the order the run group gates it: both pre flows, then both ingestion flows, then the OSDU
        // flow. The group is what decides that order, and the executor runs each member. Its OSDU member is left to this
        // test to run, so that the plan below is read while the ledger still holds nothing, which is the moment a first
        // run of a flow is actually in.
        var order = await EnqueueChainAndRunIngestionAsync(estate);
        Assert.Equal(
            [estate.Rename("recall-welllog-curves-pre"), estate.Rename("recall-welllog-pre")],
            order.Where(m => m.Wave == 0).Select(m => m.FlowName).OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.Equal(estate.DeliveryFlowName, order.Single(m => m.Wave == 2).FlowName);

        // What the ingestion flows left is what the delivery reads: one row per logging run, one per curve.
        Assert.Equal(3, await estate.CountAsync(estate.IngSchema, "WellLog"));
        Assert.Equal(logs.Sum(l => l.Curves.Count), await estate.CountAsync(estate.IngSchema, "WellLogCurve"));

        // The first plan over the loaded tables, with nothing delivered yet: every record is new, so every entry creates.
        // Rendered from the real tables, each document is the one the mapping's own fixture pins, which is what holds the
        // typing of the ingestion columns (a decimal where the fixture says a number) to the record OSDU receives.
        var flow = estate.DeliveryFlow();
        using (var runtime = await FlowRuntime.CreateAsync(estate.Engine, flow, SampleEstate.Values))
        {
            runtime.Selection = SourceSelection.Full();
            var plan = await runtime.PlanAsync();
            Assert.Equal(3, plan.Entries.Count);
            Assert.All(plan.Entries, e => Assert.Equal(PlannedAction.Create, e.Action));
            Assert.All(plan.Entries, e => Assert.Equal(LogFile, e.Origin.FileName));
            Assert.All(plan.Entries, e => Assert.Equal(1, e.ChunkCount));
            // The mapping pins a fixture for the logs whose rendering it means to hold (L-1001 and L-2001), not for every
            // sample log, so each log that has one is compared and the count is asserted: a fixture that stopped matching
            // would otherwise leave this comparing nothing and still passing.
            var compared = 0;
            foreach (var log in logs)
            {
                var entry = plan.Entries.Single(e => e.Key == log.Key);
                if (Fixture(runtime.Mapping.Mapping, log.LogId) is null)
                {
                    continue;
                }

                AssertRendersTheFixture(runtime.Mapping.Mapping, entry, log.LogId);
                compared++;
            }

            Assert.Equal(runtime.Mapping.Mapping.Fixtures.Count, compared);
        }

        // The group's last member, run now that its first plan has been read.
        await estate.DeliverAsync();

        Assert.Equal(3, estate.Protocol.Deliveries.Count);
        Assert.All(estate.Protocol.Deliveries, w => Assert.True(w.DeliverMetadata && w.DeliverPayload));
        // The payload files each record points at were opened and streamed: the fake target reads every chunk it is
        // handed, so a payload source here is a payload the delivery actually sent.
        foreach (var work in estate.Protocol.Deliveries)
        {
            Assert.NotNull(work.Payload);
            var chunk = Assert.Single(await work.Payload!.ListChunksAsync());
            Assert.EndsWith(SampleWellLogs.ChunkFileName, chunk.Path, StringComparison.Ordinal);
        }

        var submission = await LatestSubmissionAsync(estate);
        Assert.Equal(SubmissionStatus.Completed, submission.Status);
        Assert.Equal(3, submission.Planned);
        Assert.Equal(3, submission.Delivered);
        Assert.Equal(estate.RecordObject, submission.SourceObject);

        // Every delivered record names the file and row it came from, which is the chain's traceability link.
        for (var i = 0; i < logs.Count; i++)
        {
            var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[i].Key);
            Assert.NotNull(record);
            Assert.Equal(RecordStatus.Delivered, record!.Status);
            Assert.Equal(LogFile, record.SourceFileName);
            Assert.Equal(i + 1, record.SourceRowNumber);
            Assert.NotNull(record.TargetVersion);
            var attempt = Assert.Single(await estate.Ledger.ListAttemptsAsync(estate.FlowId, logs[i].Key, 5));
            Assert.Equal(AttemptOutcome.Delivered, attempt.Outcome);
            Assert.Equal(LogFile, attempt.SourceFileName);
        }

        // The whole-scope plan moved the scope's watermark, which is what the next run starts from.
        var watermark = await estate.Ledger.GetWatermarkAsync(estate.FlowId, Planner.ScopeKey(SampleEstate.Values));
        Assert.NotNull(watermark);
        Assert.Equal(submission.SubmissionId, watermark!.SubmissionId);
    }

    /// <summary>Case 2: a second run with no new file sends nothing and writes no attempt.</summary>
    [SkippableFact]
    public async Task A_re_run_with_no_new_files_skips_the_window_and_writes_no_attempt()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        Assert.Equal(3, estate.Protocol.Deliveries.Count);
        var attemptsBefore = await AttemptCountAsync(estate, logs);

        // Nothing new arrived, so the pre flows land nothing, the ingestion flows change no row, and the delivery's
        // window holds nothing: the run skips the scope without reading a record.
        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        Assert.Empty(estate.Protocol.Deliveries);
        Assert.Equal(attemptsBefore, await AttemptCountAsync(estate, logs));
        var stats = await estate.Ledger.StatsAsync(estate.FlowId, DateTime.UtcNow);
        Assert.Equal(3, stats.Delivered);
    }

    /// <summary>Case 3: a changed curve re-plans its own log and nothing else, and the record's origin does not move.</summary>
    [SkippableFact]
    public async Task A_changed_curve_updates_only_its_own_log_and_leaves_the_record_origin_on_the_log_file()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        Assert.Equal(3, estate.Protocol.Deliveries.Count);

        // A second curve file describes one curve of the first log differently. The keyed upsert moves that child row
        // only, and the delivery's candidate query brings in the record its changed child belongs to.
        var changed = logs[0] with
        {
            Curves = [logs[0].Curves[0], logs[0].Curves[1] with { Description = "Gamma ray, corrected" }, logs[0].Curves[2]],
        };
        await estate.WriteCurveFileAsync("welllog_curves_20260902.csv", [changed]);

        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        var delivered = Assert.Single(estate.Protocol.Deliveries);
        Assert.Equal(logs[0].Key, delivered.Key);

        // The record row itself never moved, so its origin still names the file that landed it.
        var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[0].Key);
        Assert.Equal(LogFile, record!.SourceFileName);
        Assert.Equal(1, record.SourceRowNumber);
        Assert.Equal(RecordStatus.Delivered, record.Status);

        // The curve row carries the newer content and the newer change stamp.
        Assert.Equal(
            "Gamma ray, corrected",
            await CurveValueAsync(estate, "curve_description", logs[0].SourceProject, logs[0].LogId, "GR"));
    }

    /// <summary>Case 4: landing the same rows again changes nothing, because the ingestion checksum leaves them alone.</summary>
    [SkippableFact]
    public async Task An_identical_re_land_leaves_the_ingestion_rows_and_the_record_origin_untouched()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        var updatedBefore = await estate.ValueAsync("WellLog", "UpdatedDate_DW", logs[0].SourceProject, logs[0].LogId);

        // The same rows arrive under a new file name. The pre table gains them, but the ingestion upsert compares the
        // business columns, finds them identical and touches nothing: not the content, not UpdatedDate_DW, not the
        // origin columns. So the delivery's window holds nothing and the record keeps the file it came from.
        await estate.WriteLogFileAsync("welllog_20260903.csv", logs);
        await estate.WriteCurveFileAsync("welllog_curves_20260903.csv", logs);

        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        Assert.Empty(estate.Protocol.Deliveries);
        Assert.Equal(3, await estate.CountAsync(estate.IngSchema, "WellLog"));
        Assert.Equal(updatedBefore, await estate.ValueAsync("WellLog", "UpdatedDate_DW", logs[0].SourceProject, logs[0].LogId));
        Assert.Equal(LogFile, await estate.ValueAsync("WellLog", "FileName_DW", logs[0].SourceProject, logs[0].LogId));
        var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[0].Key);
        Assert.Equal(LogFile, record!.SourceFileName);
    }

    /// <summary>Case 5: a row whose business version went backwards is a stale skip, never a delivery.</summary>
    [SkippableFact]
    public async Task A_row_landed_with_an_older_business_version_is_skipped_as_stale_and_never_sent()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        Assert.Equal(3, estate.Protocol.Deliveries.Count);

        // A late file carries changed content for one log under an older update_date. The ingestion flow takes it (the
        // content moved, so the row is updated and its change stamp moves with it), and the delivery refuses to send a
        // version older than the one it already delivered.
        var stale = logs[1] with { Creator = "STALE-SOURCE", UpdateDateUtc = logs[1].UpdateDateUtc.AddDays(-7) };
        await estate.WriteLogFileAsync("welllog_20260904.csv", [stale]);

        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        Assert.Empty(estate.Protocol.Deliveries);
        var submission = await LatestSubmissionAsync(estate);
        Assert.Equal(1, submission.SkippedStale);
        Assert.Equal(0, submission.Delivered);

        // The record still holds the version that was delivered, and the stale pass is on its history.
        var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[1].Key);
        Assert.Equal(RecordStatus.Delivered, record!.Status);
        Assert.Equal(SampleWellLogs.UpdatedUtc, record.SourceModifiedUtc);
        Assert.Equal(1, await estate.Ledger.CountAttemptsAsync(submission.SubmissionId, AttemptOutcome.Skipped, AttemptPhases.Stale));
    }

    /// <summary>Case 6: a fan-out over key slices plans every record exactly once and writes one watermark.</summary>
    [SkippableFact]
    public async Task A_fan_out_over_key_slices_plans_every_record_once_with_disjoint_batches_and_one_watermark()
    {
        const int Records = 2000;
        await using var estate = await SqlServerIngestionFixture.StartAsync(fanOut: 2, batchRecords: 250);
        var template = SampleWellLogs.Logs()[0];
        await estate.WritePayloadsAsync([template]);
        await estate.WriteRowsAsync("welllog", LogFile, SampleWellLogs.LogColumns, GeneratedLogs(template, Records));
        await estate.WriteRowsAsync("curves-meta", CurveFile, SampleWellLogs.CurveColumns, GeneratedCurves(template, Records));
        await estate.RunIngestionChainAsync();
        Assert.Equal(Records, await estate.CountAsync(estate.IngSchema, "WellLog"));

        // The fan-out needs a dispatcher and a run to belong to; the members run inline here, each on a runtime of its
        // own over the same engine, which is what another node would do with the slices it was dealt.
        var flow = estate.DeliveryFlow();
        var dispatcher = new InlineDispatcher(flow);
        var engine = estate.Engine with { FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "chain tests";

        var result = await runtime.RunAsync(force: false);

        Assert.Equal(2, result.IntakeMembers);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(Records, result.Submission.Planned);
        Assert.Equal(Records, result.Submission.Delivered);
        Assert.Equal(Records, estate.Protocol.Deliveries.Count);

        // Every record was planned exactly once: no key reached two members.
        Assert.Equal(Records, estate.Protocol.Deliveries.Select(d => d.Key).Distinct().Count());

        // Each member numbered its batches in a namespace of its own, so two members never wrote the same batch.
        var batches = await estate.Ledger.ListWorkBatchesAsync(result.Submission.SubmissionId, 1000, 0);
        Assert.Equal(result.Submission.BatchCount, batches.Count);
        Assert.Equal(batches.Count, batches.Select(b => b.Index).Distinct().Count());
        Assert.Contains(batches, b => b.Index >= SubmissionIntake.BatchBase(1));

        // The slices were dealt from one read, and the scope's watermark was written once, by the coordinating run.
        var intakeMembers = dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Intake).ToList();
        Assert.Equal(2, intakeMembers.Count);
        Assert.Equal(
            intakeMembers.SelectMany(e => e.Payload.Slices).Distinct().Count(),
            intakeMembers.Sum(e => e.Payload.Slices.Count));
        await using var db = estate.Context();
        var watermarks = await db.DeliverySourceWatermarks.Where(w => w.FlowId == estate.FlowId).ToListAsync();
        var watermark = Assert.Single(watermarks);
        Assert.Equal(result.Submission.SubmissionId, watermark.SubmissionId);
    }

    /// <summary>
    /// Case 8: one input, two OSDU pipelines on two versions of the WellLog schema, both running at once, each fanned out
    /// over several nodes that push rows concurrently, and each keeping its own ledger.
    /// </summary>
    [SkippableFact]
    public async Task One_input_fans_out_to_two_pipelines_on_two_schema_versions_whose_nodes_push_concurrently()
    {
        const int Records = 400;
        const int Nodes = 3;
        await using var estate = await SqlServerIngestionFixture.StartAsync(fanOut: Nodes, batchRecords: 40);
        var template = SampleWellLogs.Logs()[0];
        await estate.WritePayloadsAsync([template]);
        await estate.WriteRowsAsync("welllog", LogFile, SampleWellLogs.LogColumns, GeneratedLogs(template, Records));
        await estate.WriteRowsAsync("curves-meta", CurveFile, SampleWellLogs.CurveColumns, GeneratedCurves(template, Records));
        await estate.RunIngestionChainAsync();
        Assert.Equal(Records, await estate.CountAsync(estate.IngSchema, "WellLog"));

        // The shipped flow delivers WellLog 1.4.0 to the sample partition; the second pipeline renders the same rows with
        // the same mapping entries as WellLog 1.5.0, into a partition of its own.
        var partition = "next-" + estate.Suffix;
        var current = estate.DeliveryFlow();
        var next = WellLogVersions.OnNextVersion(current, estate.DeliveryFlowName + "-next", partition, estate.Root);
        try
        {
            await WellLogVersions.SaveNextTemplateAsync(new OsduTemplateStore(estate.Context, TimeProvider.System));
            await WellLogVersions.ImportPartitionCacheAsync(new OsduCacheStore(estate.Context), partition, estate.DeliveryFlowName + "-cache", estate.Root);

            // Each push takes a moment, so the pushes a node makes at once overlap.
            estate.Protocol.Before = (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(15), ct);
            var runs = await Task.WhenAll(RunOnNodesAsync(estate, current), RunOnNodesAsync(estate, next));

            await using var db = estate.Context();
            foreach (var (run, flow, kind, target) in new[]
            {
                (runs[0], current, WellLogVersions.CurrentKind, Samples.SampleCacheScope),
                (runs[1], next, WellLogVersions.NextKind, partition),
            })
            {
                // Planned by the coordinator and its intake nodes, sent by the drain nodes and the coordinator: every row once.
                Assert.Empty(run.Nodes.Failures);
                Assert.Equal((Nodes, Nodes), (run.Result.IntakeMembers, run.Result.DrainMembers));
                Assert.Equal(SubmissionStatus.Completed, run.Result.Submission.Status);
                Assert.Equal((Records, Records), (run.Result.Submission.Planned, run.Result.Submission.Delivered));
                var sent = estate.Protocol.Deliveries.Where(d => d.Document["kind"]!.GetValue<string>() == kind).ToList();
                Assert.Equal(Records, sent.Count);
                Assert.Equal(Records, sent.Select(d => d.Key).Distinct().Count());
                Assert.All(sent, d => Assert.StartsWith(target + ":work-product-component--WellLog:", d.TargetId, StringComparison.Ordinal));

                // The nodes ran at the same time, more than one of them pushed, and a node pushed several rows at once.
                Assert.True(run.Nodes.MaxRunning >= 2, $"{flow.Name}: at most {run.Nodes.MaxRunning} node(s) ran at once.");
                var pushed = estate.Protocol.DeliveriesByNode.Where(n => n.Key.StartsWith(flow.Name + "/", StringComparison.Ordinal)).OrderBy(n => n.Key, StringComparer.Ordinal).ToList();
                var peaks = estate.Protocol.MaxInFlightByNode;
                _output.WriteLine($"{flow.Name} ({kind}): {run.Nodes.MaxRunning} member node(s) at once; " + string.Join(", ", pushed.Select(n => $"{n.Key} pushed {n.Value}, at most {peaks[n.Key]} at once")));
                Assert.Equal(Records, pushed.Sum(n => n.Value));
                Assert.True(pushed.Count >= 2, $"{flow.Name}: only {string.Join(", ", pushed.Select(n => n.Key))} pushed.");
                var inFlight = estate.Protocol.MaxInFlightByNode.Where(n => n.Key.StartsWith(flow.Name + "/", StringComparison.Ordinal)).Max(n => n.Value);
                Assert.True(inFlight > 1, $"{flow.Name}: no node pushed more than one row at a time.");
                var batches = await estate.Ledger.ListWorkBatchesAsync(run.Result.Submission.SubmissionId, 1000, 0);
                Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
                Assert.True(batches.Select(b => b.RunId).Distinct().Count() >= 2, $"{flow.Name}: every batch was drained by one run.");

                // Each pipeline's ledger holds exactly its own records, claims and attempts, whatever the other did at the same time.
                var flowId = flow.Id;
                Assert.Equal(Records, (await estate.Ledger.StatsAsync(flowId, DateTime.UtcNow)).Delivered);
                Assert.Equal(Records, await db.DeliveryRecords.CountAsync(r => r.FlowId == flowId && r.ClaimedTargetId != null && r.ClaimedTargetId.StartsWith(target + ":")));
                Assert.Equal(Records, await db.DeliveryAttempts.CountAsync(a => a.FlowId == flowId && a.Outcome == "delivered"));
                Assert.Single(await db.DeliverySourceWatermarks.Where(w => w.FlowId == flowId).ToListAsync());
            }
        }
        finally
        {
            await estate.ForgetFlowAsync(next.Id);
            await estate.ForgetCacheAsync(partition);
        }
    }

    /// <summary>
    /// Runs the deliver operation of <paramref name="flow"/> as a coordinating run whose members are nodes of their own,
    /// all running at once. The coordinator's own pushes are counted under its node name.
    /// </summary>
    private async Task<(RunResult Result, ConcurrentNodes Nodes)> RunOnNodesAsync(SqlServerIngestionFixture estate, FlowDefinition flow)
    {
        var nodes = new ConcurrentNodes(flow, _output.WriteLine);
        var engine = estate.Engine with { FanOut = nodes };
        nodes.Engine = engine;
        FakeProtocol.CurrentNode.Value = flow.Name + "/coordinator";
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "chain tests";
        return (await runtime.RunAsync(force: false), nodes);
    }

    /// <summary>Case 7: lineage orders the estate's waves pre, then ingestion, then the OSDU flow.</summary>
    [SkippableFact]
    public async Task Lineage_orders_the_chain_pre_then_ingestion_then_the_osdu_flow()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();

        // The collector parses with the module's kinds registered, so the delivery document is read and its declared
        // reads of the ingestion tables are what put it after the flows that write them.
        var loader = YamlDocumentLoader.CreateDefault([new DeliveryFlowKind(new DeliveryDocumentLoader())]);
        var collected = new FlowSetCollector(loader).Collect(estate.FlowsDirectory);
        var report = LineageGraphBuilder.Build(collected, estate.FlowsDirectory, [LineageTier.Declared], DateTime.UtcNow);

        Assert.Empty(report.ExecutionPlan.Unordered);
        var waves = report.ExecutionPlan.Waves.ToList();
        int WaveOf(string shipped)
        {
            var name = estate.Rename(shipped);
            var wave = waves.FirstOrDefault(w => w.Flows.Contains(name, StringComparer.OrdinalIgnoreCase));
            Assert.True(wave is not null, $"'{name}' is in no wave of the plan; waves: {Describe(waves)}.");
            return wave!.Wave;
        }

        var pre = WaveOf("recall-welllog-pre");
        var curvesPre = WaveOf("recall-welllog-curves-pre");
        var ing = WaveOf("recall-welllog-ing");
        var curvesIng = WaveOf("recall-welllog-curves-ing");
        var delivery = WaveOf("recall-welllog");

        Assert.True(pre < ing, $"the pre flow must run before the ingestion flow that reads its view; waves: {Describe(waves)}");
        Assert.True(curvesPre < curvesIng, $"the curve pre flow must run before its ingestion flow; waves: {Describe(waves)}");
        Assert.True(ing < delivery, $"the OSDU flow must run after the ingestion flow that writes its record table; waves: {Describe(waves)}");
        Assert.True(curvesIng < delivery, $"the OSDU flow must run after the ingestion flow that writes its child table; waves: {Describe(waves)}");
    }

    /// <summary>
    /// Every generated document is one the platform can read. A three-part name rewritten for the test database begins
    /// with '[', which a YAML plain scalar may not, so the generator writes it quoted; this is what keeps that true, and
    /// it needs no database to say so.
    /// </summary>
    [Fact]
    public void The_generated_chain_documents_parse_as_the_platform_reads_them()
    {
        const string Database = "OsduDeliveryTest";
        const string Suffix = "abc123de";
        var root = Samples.NewTempDirectory();
        try
        {
            SqlServerIngestionFixture.GenerateEstate(root, Database, Suffix, "SQLFLOW_CHAIN_DB_" + Suffix, fanOut: 2, batchRecords: 250);

            // The loader the platform reads a repository with, knowing the module's kinds, so the delivery document is
            // parsed by the same code a node would parse it with.
            var documents = YamlDocumentLoader.CreateDefault([new DeliveryFlowKind(new DeliveryDocumentLoader())]);
            var files = Directory.EnumerateFiles(Path.Combine(root, "flows"), "*.yaml").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(5, files.Count);
            foreach (var file in files)
            {
                Assert.NotNull(DocumentLoader.Load(documents, file));
            }

            // The tables the OSDU flow reads are this database's, and they still parse as three-part names.
            var flow = new DeliveryDocumentLoader().LoadFlow(Path.Combine(root, "flows", "rw" + Suffix + ".yaml"));
            var record = SourceObjectName.Parse(flow.Source.Record.Object);
            Assert.Equal(Database, record.Database);
            Assert.Equal("ing_" + Suffix, record.Schema);
            Assert.Equal("WellLog", record.Name);
            var curves = SourceObjectName.Parse(flow.Source.Datasets["curves"].Object);
            Assert.Equal("ing_" + Suffix, curves.Schema);
            Assert.Equal("WellLogCurve", curves.Name);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A generated document a reader still holds open is left for the operating system's own cleanup.
            }
        }
    }

    private static string Describe(IReadOnlyList<LineageWave> waves)
        => string.Join(" | ", waves.Select(w => w.Wave.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", w.Flows)));

    /// <summary>
    /// Enqueues the whole chain as one wave-gated run group and runs the flows that load the ingestion tables through the
    /// document executor, in the order the queue put them in. The group is the platform's own ordering, so a test never
    /// decides for itself what runs first; its rows are removed again whatever happens.
    /// <para>The OSDU member is enqueued with the rest but not run here, and the queue's order is returned so the caller
    /// can see where it sits. A caller that runs it itself can read the flow's first plan before anything is delivered,
    /// which a helper that delivered on its way through would have made unobservable.</para>
    /// </summary>
    private static async Task<IReadOnlyList<(string FlowName, int Wave)>> EnqueueChainAndRunIngestionAsync(SqlServerIngestionFixture estate)
    {
        var connectionString = estate.ConnectionString;
        await CatalogDatabase.MigrateAsync(connectionString);
        var repoId = FlowIdentity.FromName("chain_" + estate.Suffix);
        var shipped = new Dictionary<string, string>(StringComparer.Ordinal);
        var members = new List<RunScopeMember>();
        foreach (var (name, kind, wave) in Chain())
        {
            var generated = estate.Rename(name);
            shipped[generated] = name;
            members.Add(new RunScopeMember(generated, kind, wave, CatalogPipeline.DefaultBatch));
        }

        try
        {
            List<(string FlowName, int Wave)> queued;
            await using (var db = CatalogDatabase.Create(connectionString))
            {
                var now = DateTime.UtcNow;
                foreach (var member in members)
                {
                    db.Pipelines.Add(new CatalogPipeline
                    {
                        Id = CatalogIdentity.Pipeline(repoId, member.FlowName),
                        RepoId = repoId,
                        Name = member.FlowName,
                        Kind = member.FlowKind,
                        Batch = member.Batch,
                        RelativePath = "flows/" + member.FlowName + ".yaml",
                        Active = true,
                        Wave = member.Wave,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    });
                }

                await db.SaveChangesAsync();
                var group = await RunQueueStore.EnqueueGroupAsync(
                    db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, members[0].FlowName, members), now);
                Assert.Equal(members.Count, group.RunIds.Count);
                queued = await db.Runs.AsNoTracking()
                    .Where(r => r.GroupId == group.GroupId)
                    .OrderBy(r => r.GroupWave).ThenBy(r => r.FlowName)
                    .Select(r => new ValueTuple<string, int>(r.FlowName, r.GroupWave))
                    .ToListAsync();
            }

            foreach (var (flowName, _) in queued)
            {
                var name = shipped[flowName];
                if (name != "recall-welllog")
                {
                    await estate.RunFlowAsync(name);
                }
            }

            return queued;
        }
        finally
        {
            await using var db = CatalogDatabase.Create(connectionString);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>The chain's members: the flow as the repository names it, its kind, and the wave lineage puts it in.</summary>
    private static IReadOnlyList<(string Name, string Kind, int Wave)> Chain() =>
    [
        ("recall-welllog-pre", "file", 0),
        ("recall-welllog-curves-pre", "file", 0),
        ("recall-welllog-ing", "ing", 1),
        ("recall-welllog-curves-ing", "ing", 1),
        ("recall-welllog", FlowDefinition.FlowTypeName, 2),
    ];

    /// <summary>
    /// Asserts that what the chain rendered from the real tables is exactly what the mapping's fixture pins, compared
    /// canonically so the order the two were written in never decides the outcome.
    /// </summary>
    /// <summary>The fixture this mapping pins for a log, or null when it pins none for it.</summary>
    private static MappingFixture? Fixture(MappingDefinition mapping, string logId)
        => mapping.Fixtures.SingleOrDefault(f => f.Name.StartsWith(logId, StringComparison.Ordinal));

    private static void AssertRendersTheFixture(MappingDefinition mapping, PlanEntry entry, string logId)
    {
        var fixture = Fixture(mapping, logId)
            ?? throw new InvalidOperationException($"The mapping pins no fixture for log '{logId}'.");
        Assert.NotNull(entry.Render);
        Assert.Empty(entry.Render!.Holds);
        Assert.Equal(Canonical(JsonNode.Parse(fixture.Expected)), Canonical(entry.Render.Document));
    }

    /// <summary>One JSON document as a comparable text: object members in name order, array order left as it is.</summary>
    private static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var ordered = new JsonObject();
                foreach (var (name, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    ordered[name] = value is null ? null : JsonNode.Parse(Canonical(value));
                }

                return ordered.ToJsonString();
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(item is null ? null : JsonNode.Parse(Canonical(item)));
                }

                return items.ToJsonString();
            default:
                return node?.ToJsonString() ?? "null";
        }
    }

    private static async Task<SubmissionState> LatestSubmissionAsync(SqlServerIngestionFixture estate)
    {
        var submissions = await estate.Ledger.ListSubmissionsAsync(estate.FlowId, 20);
        Assert.NotEmpty(submissions);
        return submissions.OrderByDescending(s => s.ReceivedUtc).First();
    }

    private static async Task<int> AttemptCountAsync(SqlServerIngestionFixture estate, IReadOnlyList<SampleLog> logs)
    {
        var total = 0;
        foreach (var log in logs)
        {
            total += (await estate.Ledger.ListAttemptsAsync(estate.FlowId, log.Key, 50)).Count;
        }

        return total;
    }

    private static async Task<object?> CurveValueAsync(SqlServerIngestionFixture estate, string column, string project, string logId, string curveId)
    {
        await using var db = estate.Context();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SET QUOTED_IDENTIFIER ON; SELECT [{column}] FROM [{estate.IngSchema}].[WellLogCurve] WHERE [source_project] = @p AND [log_id] = @l AND [curve_id] = @c;";
        AddParameter(command, "@p", project);
        AddParameter(command, "@l", logId);
        AddParameter(command, "@c", curveId);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// The record rows of a generated estate: one logging run per record, all pointing at the same payload folder, so a
    /// fan-out has thousands of keys to slice without thousands of folders behind them.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> GeneratedLogs(SampleLog template, int count)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new Dictionary<string, string?>(SampleWellLogs.LogRow(template), StringComparer.Ordinal);
            var logId = string.Create(CultureInfo.InvariantCulture, $"L-{i:D5}");
            row["log_id"] = logId;
            row["native_uid"] = template.SourceProject + ":" + logId;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>One index curve per generated record, which is what its document's reference curve is read from.</summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> GeneratedCurves(SampleLog template, int count)
    {
        var first = SampleWellLogs.CurveRows(template)[0];
        var rows = new List<IReadOnlyDictionary<string, string?>>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new Dictionary<string, string?>(first, StringComparer.Ordinal);
            row["log_id"] = string.Create(CultureInfo.InvariantCulture, $"L-{i:D5}");
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// A fan-out whose members run inline, each on its own runtime over the same engine, as another node of the pool
    /// would run the slices it was dealt.
    /// </summary>
    private sealed class InlineDispatcher : IFanOutDispatcher
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly FlowDefinition _flow;
        private readonly Dictionary<Guid, FanOutMemberState> _members = [];
        private readonly Dictionary<Guid, List<Guid>> _groups = [];

        public InlineDispatcher(FlowDefinition flow)
        {
            _flow = flow;
        }

        public EngineContext? Engine { get; set; }

        public List<(string Operation, DeliveryRunPayload Payload)> Enqueued { get; } = [];

        public bool Available => true;

        public async Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(members);
            var groupId = Guid.NewGuid();
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                var runId = Guid.NewGuid();
                ids.Add(runId);
                var (operation, payload, resultJson) = await RunMemberAsync(Engine!, _flow, member, runId, ct);
                Enqueued.Add((operation, payload));
                _members[runId] = new FanOutMemberState(runId, ids.Count, "succeeded", null, resultJson);
            }

            _groups[groupId] = ids;
            return new FanOutHandle(groupId, ids);
        }

        public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);
            return Task.FromResult(new FanOutState(_groups[handle.GroupId].Select(id => _members[id]).ToList()));
        }

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>
        /// Runs one member as a node runs it: an intake of the slices it was dealt, or a drain of the submission, on a runtime
        /// of its own over <paramref name="engine"/>. Returns what the node journals as the member's result.
        /// </summary>
        public static async Task<(string Operation, DeliveryRunPayload Payload, string ResultJson)> RunMemberAsync(
            EngineContext engine, FlowDefinition flow, RunParameters member, Guid runId, CancellationToken ct)
        {
            var operation = DeliveryOperations.Of(member);
            var payload = DeliveryRunPayload.Parse(member);
            payload.Validate(operation);
            if (operation == DeliveryOperations.Intake)
            {
                using var runtime = await FlowRuntime.CreateAsync(engine, flow, member.Values, ct);
                runtime.RunId = runId;
                runtime.SubmissionId = payload.SubmissionId;
                runtime.Slices = payload.Slices;
                var intake = await runtime.IntakeAsync(payload.Force, ct);
                return (operation, payload, JsonSerializer.Serialize(
                    IntakeOutcome.From(intake, flow.Source.Record.Object, runtime.Selection.Describe(), payload.Slices), Json));
            }

            using var target = FlowRuntime.ForTarget(engine, flow);
            target.RunId = runId;
            var drained = await target.WorkAsync(once: false, payload.SubmissionId, ct);
            return (operation, payload, JsonSerializer.Serialize(DrainOutcome.From(drained, payload.SubmissionId), Json));
        }
    }

    /// <summary>
    /// A fan-out whose members run as nodes of their own, all at once: each member starts on its own flow of control the
    /// moment it is enqueued, on a runtime of its own over the same engine, while the coordinating run carries on with its
    /// own work, as the nodes of a pool would. A member's pushes are counted under its node's name
    /// (<see cref="FakeProtocol.CurrentNode"/>). Reading the state waits for the members to finish, which a coordinator
    /// does by polling; a member that failed is reported failed, and named in <see cref="Failures"/>.
    /// </summary>
    private sealed class ConcurrentNodes : IFanOutDispatcher
    {
        private readonly FlowDefinition _flow;
        private readonly Action<string> _log;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, (int Slot, Task<string> Run)> _members = [];
        private readonly Dictionary<Guid, IReadOnlyList<Guid>> _groups = [];
        private readonly List<string> _failures = [];
        private int _nodes;
        private int _running;
        private int _maxRunning;

        /// <param name="flow">The flow whose members the nodes run.</param>
        /// <param name="log">Where a failed member's whole error, with its stack, is written.</param>
        public ConcurrentNodes(FlowDefinition flow, Action<string> log)
        {
            _flow = flow;
            _log = log;
        }

        public EngineContext? Engine { get; set; }

        public bool Available => true;

        /// <summary>The most members that were running at the same moment.</summary>
        public int MaxRunning
        {
            get
            {
                lock (_gate)
                {
                    return _maxRunning;
                }
            }
        }

        /// <summary>The members that failed, each with its node and error.</summary>
        public IReadOnlyList<string> Failures
        {
            get
            {
                lock (_gate)
                {
                    return [.. _failures];
                }
            }
        }

        public Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(members);
            var engine = Engine ?? throw new InvalidOperationException("The nodes have no engine to run their members on.");
            var ids = new List<Guid>();
            lock (_gate)
            {
                foreach (var member in members)
                {
                    var runId = Guid.NewGuid();
                    var node = $"{_flow.Name}/node-{++_nodes}";
                    ids.Add(runId);
                    _members[runId] = (ids.Count, Task.Run(() => RunAsync(engine, member, runId, node, ct), CancellationToken.None));
                }

                var groupId = Guid.NewGuid();
                _groups[groupId] = ids;
                return Task.FromResult(new FanOutHandle(groupId, ids));
            }
        }

        public async Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);
            List<(Guid RunId, int Slot, Task<string> Run)> runs;
            lock (_gate)
            {
                runs = _groups[handle.GroupId].Select(id => (id, _members[id].Slot, _members[id].Run)).ToList();
            }

            // Every member settles before its state is read; a failed member's error is its state, not this call's.
            await Task.WhenAll(runs.Select(r => r.Run.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default))).WaitAsync(ct);
            return new FanOutState(runs
                .Select(r => r.Run.IsCompletedSuccessfully
                    ? new FanOutMemberState(r.RunId, r.Slot, "succeeded", null, r.Run.Result)
                    : new FanOutMemberState(r.RunId, r.Slot, "failed", r.Run.Exception?.GetBaseException().Message ?? "cancelled", null))
                .ToList());
        }

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default) => Task.CompletedTask;

        private async Task<string> RunAsync(EngineContext engine, RunParameters member, Guid runId, string node, CancellationToken ct)
        {
            FakeProtocol.CurrentNode.Value = node;
            lock (_gate)
            {
                _maxRunning = Math.Max(_maxRunning, ++_running);
            }

            try
            {
                return (await InlineDispatcher.RunMemberAsync(engine, _flow, member, runId, ct)).ResultJson;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failures.Add($"{node}: {ex.GetType().Name}: {ex.Message}");
                    _log($"{node} failed: {ex}");
                }

                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _running--;
                }
            }
        }
    }
}
