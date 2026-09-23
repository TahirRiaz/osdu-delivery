using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The engine at scale, in miniature: work batches, step resumption, and the fan-out choreography.</summary>
public class ScaleEngineTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DeliveryWorker Worker(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger, string id = "test-worker")
        => new(ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), id) { MaxWait = null };

    /// <summary>The sample logs in the ingestion tables, with their payload files written under this test's root.</summary>
    private async Task<MemoryIngestionTables> EstateAsync()
        => await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime);

    [Fact]
    public async Task A_run_writes_work_batches_and_records_every_step_and_returned_value()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables);
        var local = Samples.LocalFlow(_root);
        var flow = local with { Reliability = local.Reliability with { BatchRecords = 2 } };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var protocol = new FakeProtocol();

        var intake = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var submissionId = intake.Submission.SubmissionId;
        Assert.Equal(3, intake.Counts.Planned);
        Assert.Equal(2, intake.Counts.Batches);
        var submission = (await ledger.GetSubmissionAsync(submissionId))!;
        Assert.Equal(2, submission.BatchCount);
        Assert.Equal(SubmissionStatus.Planned, submission.Status);
        // The first run of a scope has no watermark, so its window opens at the beginning; it is still an incremental
        // read, and it is the watermark it writes that lets the next run start where this one stopped.
        Assert.Equal(SubmissionKinds.Incremental, submission.Kind);
        Assert.Equal(flow.Source.Record.Object, submission.SourceObject);
        Assert.NotNull(submission.WorkLocation);
        Assert.True(File.Exists(WorkBatchFile.PathFor(submission.WorkLocation!, submissionId, 0)));
        Assert.True(File.Exists(WorkBatchFile.PathFor(submission.WorkLocation!, submissionId, 1)));
        var pending = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.NotNull(pending!.PendingDocumentRef);
        Assert.NotNull(pending.WorkBatch);
        // The origin of the queued version: the file and row the ingestion tables carry for it.
        Assert.Equal(SampleEstate.FileName, pending.PendingSourceFileName);
        Assert.Equal(1, pending.PendingSourceRowNumber);

        var summary = await Worker(runtime, protocol, ledger).DrainAsync(submissionId);
        Assert.Equal(3, summary.Delivered);
        Assert.Equal(2, summary.Batches);
        var batches = await ledger.ListWorkBatchesAsync(submissionId, 10, 0);
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
        Assert.Equal(3, batches.Sum(b => b.Delivered));
        Assert.All(batches, b => Assert.Null(b.LeaseOwner));

        var delivered = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Null(delivered.PendingDocumentRef);
        Assert.Null(delivered.WorkBatch);
        Assert.Equal(SampleEstate.FileName, delivered.SourceFileName);
        Assert.Contains("\"recordId\":\"" + delivered.TargetId, delivered.TargetStateJson, StringComparison.Ordinal);
        Assert.Contains("\"version\"", delivered.TargetStateJson, StringComparison.Ordinal);

        var attempts = await ledger.ListAttemptsAsync(flow.Id, SampleEstate.Key(0), 5);
        var attempt = Assert.Single(attempts);
        Assert.NotNull(attempt.WorkBatch);
        Assert.Equal(SampleEstate.FileName, attempt.SourceFileName);
        using var result = JsonDocument.Parse(attempt.ResultJson!);
        Assert.Equal("fake", result.RootElement.GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.Equal(200, result.RootElement.GetProperty("steps")[0].GetProperty("status").GetInt32());
        Assert.Equal(delivered.TargetId, result.RootElement.GetProperty("returned").GetProperty("recordId").GetString());

        var closed = await runtime.Intake.CompleteAsync(submissionId, flow.Id);
        Assert.Equal(SubmissionStatus.Completed, closed.Status);
        Assert.Equal(3, closed.Delivered);
    }

    [Fact]
    public async Task A_retry_resumes_after_the_steps_the_first_try_completed()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables);
        var flow = Samples.LocalFlow(_root);
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var protocol = new FakeProtocol();
        var resumed = 0;
        protocol.Before = async (work, ct) =>
        {
            if (work.Completed("metadata") is { } done)
            {
                Assert.Equal("41", done["version"]);
                resumed++;
                return;
            }

            await work.ReportStepAsync("metadata", new Dictionary<string, string> { ["version"] = "41", ["recordId"] = work.TargetId }, ct);
            throw new OsduStatusException(503, "HTTP 503 Service Unavailable");
        };

        var intake = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var submissionId = intake.Submission.SubmissionId;
        var first = await Worker(runtime, protocol, ledger).DrainAsync(submissionId);
        Assert.Equal(3, first.Retried);
        var waiting = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.Equal(RecordStatus.Pending, waiting!.Status);
        Assert.Contains("\"metadata\"", waiting.PendingStepJson, StringComparison.Ordinal);
        Assert.NotNull(await ledger.NextDueAsync(flow.Id, submissionId, _clock.GetUtcNow().UtcDateTime));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await Worker(runtime, protocol, ledger, "resumer").DrainAsync(submissionId);
        Assert.Equal(3, second.Delivered);
        Assert.Equal(3, resumed);
        var delivered = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Null(delivered.PendingStepJson);
        var attempts = await ledger.ListAttemptsAsync(flow.Id, SampleEstate.Key(0), 5);
        Assert.Equal(2, attempts.Count);
        Assert.Contains("\"resumed\":true", attempts[0].ResultJson, StringComparison.Ordinal);
        Assert.Equal(AttemptOutcome.Failed, attempts[1].Outcome);
        Assert.Contains("\"metadata\"", attempts[1].ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deliver_run_fans_its_intake_and_drains_out_and_completes_the_submission()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var local = Samples.LocalFlow(_root);
        var flow = local with { Reliability = local.Reliability with { FanOut = 2, FanOutMinRecords = 1, BatchRecords = 1 } };
        var dispatcher = new InlineDispatcher(flow);
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);
        var submissionId = result.Submission.SubmissionId;

        // Three candidate records at one record per batch: three slices, dealt as one share each to the coordinating run
        // and its two members.
        Assert.Equal(2, result.IntakeMembers);
        Assert.Equal(2, result.DrainMembers);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(3, result.Submission.Planned);
        Assert.Equal(3, result.Submission.Delivered);
        Assert.Equal(3, result.Submission.BatchCount);
        Assert.Equal(3, result.Submission.Slices);
        Assert.Equal("RecId", SourceWindowDescription.Parse(result.Submission.SourceWindowJson)!.SlicedOn);
        Assert.Equal(3, protocol.Deliveries.Count);

        var intakeMembers = dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Intake).ToList();
        Assert.Equal(2, intakeMembers.Count);
        Assert.All(intakeMembers, e => Assert.Equal(submissionId, e.Payload.SubmissionId));
        // Every member plans a share of its own, and no slice is planned twice.
        Assert.All(intakeMembers, e => Assert.NotEmpty(e.Payload.Slices));
        Assert.Equal(intakeMembers.SelectMany(e => e.Payload.Slices).Distinct().Count(), intakeMembers.Sum(e => e.Payload.Slices.Count));
        Assert.Equal(2, dispatcher.Enqueued.Count(e => e.Operation == DeliveryOperations.Drain));
        Assert.All(dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Drain), e => Assert.Equal(submissionId, e.Payload.SubmissionId));

        var batches = await ledger.ListWorkBatchesAsync(submissionId, 10, 0);
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
        // The member's batch numbers live in their slice's namespace, never colliding with the root's.
        Assert.Contains(batches, b => b.Index >= SubmissionIntake.BatchBase(1));
        Assert.Contains(batches, b => b.Index < SubmissionIntake.BatchBase(1));

        var activities = await ledger.ListActivitiesAsync(new ActivityQuery { SubmissionId = submissionId, Max = 20 });
        Assert.Contains(activities, a => a.Kind == "deliver" && a.Outcome == "completed");
        Assert.Contains(activities, a => a.Kind == "intake" && a.Outcome == "completed");
        Assert.Contains(activities, a => a.Kind == "drain" && a.Outcome == "completed");
    }

    /// <summary>A fan-out that runs every member inline, on its own runtime over the same engine, as another node would.</summary>
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
            var groupId = Guid.NewGuid();
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                var operation = DeliveryOperations.Of(member);
                var payload = DeliveryRunPayload.Parse(member);
                payload.Validate(operation);
                Enqueued.Add((operation, payload));
                var runId = Guid.NewGuid();
                ids.Add(runId);
                string resultJson;
                if (operation == DeliveryOperations.Intake)
                {
                    using var runtime = await FlowRuntime.CreateAsync(Engine!, _flow, member.Values, ct);
                    runtime.RunId = runId;
                    runtime.SubmissionId = payload.SubmissionId;
                    runtime.Slices = payload.Slices;
                    var intake = await runtime.IntakeAsync(payload.Force, ct);
                    resultJson = JsonSerializer.Serialize(
                        IntakeOutcome.From(intake, _flow.Source.Record.Object, runtime.Selection.Describe(), payload.Slices), Json);
                }
                else
                {
                    using var runtime = FlowRuntime.ForTarget(Engine!, _flow);
                    runtime.RunId = runId;
                    var drained = await runtime.WorkAsync(once: false, payload.SubmissionId, ct);
                    resultJson = JsonSerializer.Serialize(DrainOutcome.From(drained, payload.SubmissionId), Json);
                }

                _members[runId] = new FanOutMemberState(runId, ids.Count, "succeeded", null, resultJson);
            }

            _groups[groupId] = ids;
            return new FanOutHandle(groupId, ids);
        }

        public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
            => Task.FromResult(new FanOutState(_groups[handle.GroupId].Select(id => _members[id]).ToList()));

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory a reader still holds open is left for the operating system to reclaim.
        }
    }
}
