using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using SqlFlow.Execution;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Inline records through the run a node executes, end to end: the delivery executor over the wellbore estate, a SQLite
/// ledger and a fake protocol. What is asserted is that a submission of records takes the same path a drop does, with
/// the same change detection, the same ledger trail and the same operator actions on top of it.
/// </summary>
public sealed class InlineSubmissionEngineTests : IDisposable
{
    private readonly WellboreEstate _estate = new();
    private readonly SqliteCatalog _db = new();
    private readonly FakeProtocol _protocol = new();
    private readonly ServiceProvider _services;
    private readonly EngineContext _engine;
    private readonly CatalogLedger _ledger;

    public InlineSubmissionEngineTests()
    {
        _ledger = _db.Ledger();
        _engine = Samples.Engine(_ledger, protocols: new FakeProtocolFactory(_protocol));
        _services = new ServiceCollection().AddSingleton(_engine).BuildServiceProvider();
    }

    [Fact]
    public async Task Records_are_delivered_by_the_regular_run_and_the_ledger_says_what_was_sent()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(
            WellboreEstate.Wellbore("WB-INLINE-1", "first", "2026-09-12T10:00:00Z", "ONE-A", "ONE-B"),
            WellboreEstate.Wellbore("WB-INLINE-2", "second", "2026-09-12T10:00:00Z")));

        var outcome = await DeliverAsync(submission);
        Assert.Equal(2, outcome.Delivered);
        Assert.Equal(2, outcome.Planned);
        Assert.Equal(2, _protocol.Deliveries.Count);

        // The document is the mapping's work: master data with the aliases the record carried.
        var key = WellboreEstate.Key("WB-INLINE-1");
        var document = _protocol.Deliveries.Single(w => w.Key == key).Document;
        Assert.Equal("WB-INLINE-1", document["data"]!["FacilityName"]!.GetValue<string>());
        Assert.Equal("first", document["data"]!["FacilityDescription"]!.GetValue<string>());
        Assert.Equal(["ONE-A", "ONE-B"], document["data"]!["NameAliases"]!.AsArray().Select(a => a!["AliasName"]!.GetValue<string>()));
        Assert.Equal("opendes:master-data--Wellbore:" + key.Value.ToString("N"), _protocol.Deliveries.Single(w => w.Key == key).TargetId);

        var location = InlineDrop.Location(_estate.Definition, WellboreEstate.Values, submission.SubmissionId);
        var registered = await _ledger.GetSubmissionAsync(submission.SubmissionId);
        Assert.Equal(SubmissionStatus.Completed, registered!.Status);
        Assert.Equal(location, registered.DropLocation);
        Assert.Equal(WellboreEstate.MappingReference, registered.MappingReference);
        Assert.Equal(2, registered.Delivered);

        var stored = await _ledger.GetInlineSubmissionAsync(submission.SubmissionId);
        Assert.Equal(location, stored!.DropLocation);
        Assert.NotNull(stored.WrittenUtc);
        Assert.Equal("api:test-source", stored.ReceivedBy);

        var record = await _ledger.GetRecordAsync(_estate.Definition.Id, key);
        Assert.Equal(RecordStatus.Delivered, record!.Status);
        Assert.Equal(submission.SubmissionId, record.LastSubmissionId);
        Assert.Equal("WB-INLINE-1", record.Label);
        Assert.NotNull(record.TargetVersion);
        Assert.Equal(new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc), record.SourceModifiedUtc);
    }

    [Fact]
    public async Task The_change_gates_apply_to_records_exactly_as_they_do_to_a_drop()
    {
        var first = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-GATES", "first", "2026-09-12T10:00:00Z")));
        Assert.Equal(1, (await DeliverAsync(first)).Delivered);

        // The same record again, under a new submission: nothing renders differently, so nothing is sent.
        var unchanged = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-GATES", "first", "2026-09-12T10:00:00Z")));
        var second = await DeliverAsync(unchanged);
        Assert.Equal(0, second.Delivered);
        Assert.Equal(1, second.SkippedUnchanged);
        Assert.Single(_protocol.Deliveries);

        // A later moment with a changed description is delivered.
        var changed = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-GATES", "corrected", "2026-09-12T11:00:00Z")));
        Assert.Equal(1, (await DeliverAsync(changed)).Delivered);
        Assert.Equal(2, _protocol.Deliveries.Count);

        // An older moment never takes OSDU back.
        var older = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-GATES", "an older copy", "2026-09-12T09:00:00Z")));
        var stale = await DeliverAsync(older);
        Assert.Equal(0, stale.Delivered);
        Assert.Equal(1, stale.SkippedStale);
        Assert.Equal(2, _protocol.Deliveries.Count);

        // Forcing lifts the whole-run gates, not the per-record hashes.
        var forced = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-GATES", "corrected", "2026-09-12T11:00:00Z")), force: true);
        Assert.Equal(0, (await DeliverAsync(forced, force: true)).Delivered);
        Assert.Equal(2, _protocol.Deliveries.Count);
    }

    [Fact]
    public async Task A_plan_of_records_renders_them_and_changes_nothing()
    {
        var submission = await AcceptAsync(
            WellboreEstate.Records(WellboreEstate.Wellbore("WB-PLAN", "planned", "2026-09-12T10:00:00Z", "P-A")),
            operation: RunParameters.PlanOperation);

        var result = await RunAsync(new RunParameters { Operation = RunParameters.PlanOperation, SubmissionId = submission.SubmissionId });
        var plan = Assert.IsType<PlanOutcome>(result.Result);
        Assert.True(result.Success, result.Error);
        Assert.Equal(1, plan.Records);
        Assert.Equal(1, plan.Deliveries);
        Assert.Empty(plan.Issues);
        Assert.Empty(_protocol.Deliveries);
        Assert.Null(await _ledger.GetRecordAsync(_estate.Definition.Id, WellboreEstate.Key("WB-PLAN")));
        Assert.Null(await _ledger.GetSubmissionAsync(submission.SubmissionId));

        // The drop is written even for a plan, and the ledger says where, so what was rendered can be inspected.
        var stored = await _ledger.GetInlineSubmissionAsync(submission.SubmissionId);
        Assert.NotNull(stored!.DropLocation);
        Assert.True(File.Exists(Path.Combine(stored.DropLocation, "manifest.json")));
    }

    [Fact]
    public async Task A_rerun_writes_the_drop_again_from_the_ledger_and_sends_nothing_new()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-RERUN", "once", "2026-09-12T10:00:00Z")));
        Assert.Equal(1, (await DeliverAsync(submission)).Delivered);
        var location = InlineDrop.Location(_estate.Definition, WellboreEstate.Values, submission.SubmissionId);
        Directory.Delete(location, recursive: true);

        var rerun = await RunAsync(new RunParameters { SubmissionId = submission.SubmissionId });
        Assert.True(rerun.Success, rerun.Error);
        Assert.Equal(0, Assert.IsType<DeliverOutcome>(rerun.Result).Delivered);
        Assert.True(File.Exists(Path.Combine(location, "manifest.json")));
        Assert.Single(_protocol.Deliveries);
    }

    [Fact]
    public async Task A_record_whose_natural_key_is_incomplete_is_left_untracked_and_the_rest_are_delivered()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(
            WellboreEstate.Wellbore("WB-TRACKED", "tracked", "2026-09-12T10:00:00Z"),
            WellboreEstate.Wellbore(null, "untracked", "2026-09-12T10:00:00Z")));

        var outcome = await DeliverAsync(submission);
        Assert.Equal(1, outcome.Delivered);
        Assert.Equal(WellboreEstate.Key("WB-TRACKED"), Assert.Single(_protocol.Deliveries).Key);
        Assert.Equal(1, await CountRecordsAsync());
    }

    [Fact]
    public async Task A_record_that_declares_a_key_that_is_not_its_own_is_held()
    {
        var row = WellboreEstate.Row("WB-MISMATCH", "wrong key", "2026-09-12T10:00:00Z");
        row[DropReader.DeliveryKeyColumn] = Guid.Parse("11111111-2222-3333-4444-555555555555").ToString("D");
        var submission = await AcceptAsync(WellboreEstate.Records(new { record = row }));

        var outcome = await DeliverAsync(submission);
        Assert.Equal(0, outcome.Delivered);
        Assert.Equal(1, outcome.Held);
        Assert.Empty(_protocol.Deliveries);
        // The record is held under the key its natural key derives, which is the one that says who it is; the key it
        // declared is what it is held for.
        var held = await _ledger.GetRecordAsync(_estate.Definition.Id, WellboreEstate.Key("WB-MISMATCH"));
        Assert.Equal(RecordStatus.Held, held!.Status);
        Assert.Contains("key", held.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_record_twice_in_one_submission_fails_the_run_and_sends_nothing()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(
            WellboreEstate.Wellbore("WB-DUP", "one", "2026-09-12T10:00:00Z"),
            WellboreEstate.Wellbore("WB-DUP", "two", "2026-09-12T11:00:00Z")));

        var result = await RunAsync(new RunParameters { SubmissionId = submission.SubmissionId });
        Assert.False(result.Success);
        Assert.Contains("are the same record", result.Error, StringComparison.Ordinal);
        Assert.Empty(_protocol.Deliveries);
        Assert.Equal(0, await CountRecordsAsync());
    }

    [Fact]
    public async Task A_run_on_a_submission_of_records_takes_no_drop_location()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-DROP", "x", "2026-09-12T10:00:00Z")));

        var result = await RunAsync(new RunParameters { SubmissionId = submission.SubmissionId, Drop = Path.Combine(_estate.Root, "elsewhere") });
        Assert.False(result.Success);
        Assert.Contains("takes no drop location", result.Error, StringComparison.Ordinal);
        Assert.Empty(_protocol.Deliveries);
    }

    [Fact]
    public async Task A_submission_for_a_mapping_the_flow_no_longer_pins_is_refused_like_a_drop()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-PROMOTED", "x", "2026-09-12T10:00:00Z")));

        // The flow is promoted after the records were accepted: the manifest names the mapping they were accepted for.
        _estate.WriteMapping("Wellbore@1.1.0", File.ReadAllText(Path.Combine(_estate.Root, "mappings", "Wellbore@1.0.0.yaml")).Replace("version: 1.0.0", "version: 1.1.0", StringComparison.Ordinal));
        _estate.WriteFlow(WellboreEstate.FlowName, _estate.Flow(WellboreEstate.FlowName, "Wellbore@1.1.0"));

        var result = await RunAsync(new RunParameters { SubmissionId = submission.SubmissionId });
        Assert.False(result.Success);
        Assert.Contains("prepared for mapping 'Wellbore@1.0.0'", result.Error, StringComparison.Ordinal);
        Assert.Empty(_protocol.Deliveries);
    }

    [Fact]
    public async Task A_record_delivered_from_records_can_be_redelivered_verified_and_released_like_any_other()
    {
        var submission = await AcceptAsync(WellboreEstate.Records(WellboreEstate.Wellbore("WB-OPS", "ops", "2026-09-12T10:00:00Z")));
        Assert.Equal(1, (await DeliverAsync(submission)).Delivered);
        var key = WellboreEstate.Key("WB-OPS");

        // Redeliver: the API scopes the run to the record's last submission, which is this one; the run writes the drop
        // again from the ledger and sends the record.
        var redelivered = await RunAsync(new RunParameters
        {
            SubmissionId = submission.SubmissionId,
            RecordKeys = [key.Value],
            Redeliver = RunParameters.RedeliverAll,
        });
        Assert.True(redelivered.Success, redelivered.Error);
        Assert.Equal(1, Assert.IsType<DeliverOutcome>(redelivered.Result).Delivered);
        Assert.Equal(2, _protocol.Deliveries.Count);

        // Verify reads the record back and finds what the ledger holds.
        var verify = await RunAsync(new RunParameters { Operation = RunParameters.VerifyOperation, Force = true, RecordKeys = [key.Value] });
        var summary = Assert.IsType<VerifyRunOutcome>(verify.Result);
        Assert.Equal(1, summary.Checked);
        Assert.Equal(1, summary.Matched);
    }

    [Fact]
    public async Task A_submission_of_many_records_is_delivered_in_batches()
    {
        var many = Enumerable.Range(0, 250)
            .Select(i => WellboreEstate.Wellbore($"WB-MANY-{i:D4}", $"many {i}", "2026-09-12T10:00:00Z", $"A-{i:D4}"))
            .ToArray();
        var submission = await AcceptAsync(WellboreEstate.Records(many));

        var outcome = await DeliverAsync(submission);
        Assert.Equal(250, outcome.Delivered);
        Assert.Equal(250, _protocol.Deliveries.Count);
        Assert.True(outcome.Batches >= 1);
        Assert.Equal(250, await CountRecordsAsync());
        var registered = await _ledger.GetSubmissionAsync(submission.SubmissionId);
        Assert.Equal(250, registered!.Delivered);
    }

    /// <summary>
    /// The whole point of pointing at files rather than carrying them: the run delivers a payload the submission never
    /// uploaded, read from where the source said it already was.
    /// </summary>
    [Fact]
    public async Task Records_that_point_at_payload_files_deliver_them_from_where_they_already_are()
    {
        var (flow, file) = PayloadFlow();
        var location = _estate.WritePayloadFiles("WB-PAYLOAD", "depth,value\n0,1\n", "depth,value\n1,2\n");
        var submission = await AcceptAsync(
            WellboreEstate.Records(WellboreEstate.WellboreWithFiles("WB-PAYLOAD", "with files", "2026-09-12T10:00:00Z", location)),
            definition: flow);

        var outcome = await DeliverAsync(submission, flow, file);
        Assert.Equal(1, outcome.Delivered);
        var work = Assert.Single(_protocol.Deliveries);
        Assert.True(work.DeliverPayload);
        Assert.NotNull(work.Payload);
        Assert.Equal(WellboreEstate.Key("WB-PAYLOAD"), work.Key);
    }

    [Fact]
    public async Task A_record_whose_files_are_not_where_it_said_is_held_rather_than_delivered()
    {
        var (flow, file) = PayloadFlow();
        var submission = await AcceptAsync(
            WellboreEstate.Records(WellboreEstate.WellboreWithFiles("WB-EMPTY", "nothing there", "2026-09-12T10:00:00Z", _estate.Lake + "/WB-EMPTY")),
            definition: flow);

        var outcome = await DeliverAsync(submission, flow, file);
        Assert.Equal(0, outcome.Delivered);
        Assert.Empty(_protocol.Deliveries);
    }

    [Fact]
    public async Task A_submission_id_that_is_not_in_the_ledger_fails_the_run()
    {
        var result = await RunAsync(new RunParameters { SubmissionId = Guid.NewGuid() });
        Assert.False(result.Success);
        Assert.Contains("is not in the ledger", result.Error, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        _estate.Dispose();
    }

    private async Task<int> CountRecordsAsync()
    {
        await using var db = _db.CreateDbContext();
        var flowId = _estate.Definition.Id;
        return db.DeliveryRecords.Count(r => r.FlowId == flowId);
    }

    /// <summary>A wellbore flow that streams payload files, with where its document sits so a run can execute it.</summary>
    private (FlowDefinition Definition, string File) PayloadFlow(string name = WellboreEstate.PayloadFlowName, bool hashDetect = false)
    {
        var file = _estate.WriteFlow(name, _estate.PayloadFlow(name, hashDetect: hashDetect));
        return (WellboreEstate.Load(file), file);
    }

    private async Task<InlineSubmissionState> AcceptAsync(
        string records, string operation = RunParameters.DeliverOperation, bool force = false, FlowDefinition? definition = null)
    {
        var flow = definition ?? _estate.Definition;
        var submission = InlineSubmissionState.Accept(
            Guid.CreateVersion7(), flow, operation, force, FlowParameters.Resolve(flow, WellboreEstate.Values),
            InlineRecords.Parse(records), DateTime.UtcNow, "api:test-source");
        await using var db = _db.CreateDbContext();
        db.DeliveryInlineSubmissions.Add(InlineSubmissionRows.ToEntity(submission));
        await db.SaveChangesAsync();
        return submission;
    }

    private async Task<DeliverOutcome> DeliverAsync(
        InlineSubmissionState submission, FlowDefinition? definition = null, string? flowFile = null, bool force = false)
    {
        var result = await RunAsync(new RunParameters { SubmissionId = submission.SubmissionId, Force = force }, definition, flowFile);
        Assert.True(result.Success, result.Error);
        return Assert.IsType<DeliverOutcome>(result.Result);
    }

    private Task<DocumentExecutionResult> RunAsync(RunParameters parameters, FlowDefinition? definition = null, string? flowFile = null)
        => new DeliveryExecutor(_services).ExecuteAsync(
            new DeliveryFlowDocument { Flow = definition ?? _estate.Definition },
            flowFile ?? _estate.FlowFile,
            new DocumentExecutionOptions { RunId = Guid.CreateVersion7(), Parameters = parameters, Actor = "test:operator" },
            CancellationToken.None);
}
