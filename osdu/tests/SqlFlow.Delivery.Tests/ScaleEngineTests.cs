using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.SampleDrop;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The engine at scale, in miniature: work batches, step resumption, and the fan-out choreography.</summary>
public class ScaleEngineTests : IDisposable
{
    private static readonly Guid Submission1 = new("44444444-4444-4444-4444-444444444444");

    private readonly SqliteCatalog _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private async Task<string> DropAsync(string name, IReadOnlyList<SampleRecord> records, Guid submission, int partitions = 1)
    {
        var dir = Path.Combine(_root, name);
        await SampleDropBuilder.WriteAsync(dir, "STAT_COMP", records, submission, 1, partitions);
        return dir;
    }

    private DeliveryWorker Worker(FlowRuntime runtime, FakeProtocol protocol, CatalogLedger ledger, string id = "test-worker")
        => new(ledger, runtime.Context.Drops, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), id) { MaxWait = null };

    [Fact]
    public async Task A_run_writes_work_batches_and_records_every_step_and_returned_value()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("batches", records, Submission1);
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = Samples.LocalFlow(drop) with { Reliability = Samples.LocalFlow(drop).Reliability with { BatchRecords = 2 } };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, new Dictionary<string, string> { ["logSource"] = "STAT_COMP" }, drop);
        var protocol = new FakeProtocol();

        var intake = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, drop, force: false);
        Assert.Equal(3, intake.Counts.Planned);
        Assert.Equal(2, intake.Counts.Batches);
        var submission = (await ledger.GetSubmissionAsync(Submission1))!;
        Assert.Equal(2, submission.BatchCount);
        Assert.Equal(SubmissionStatus.Planned, submission.Status);
        Assert.NotNull(submission.WorkLocation);
        Assert.True(File.Exists(WorkBatchFile.PathFor(submission.WorkLocation!, Submission1, 0)));
        Assert.True(File.Exists(WorkBatchFile.PathFor(submission.WorkLocation!, Submission1, 1)));
        var pending = await ledger.GetRecordAsync(flow.Id, records[0].Key);
        Assert.NotNull(pending!.PendingDocumentRef);
        Assert.NotNull(pending.WorkBatch);

        var summary = await Worker(runtime, protocol, ledger).DrainAsync(Submission1);
        Assert.Equal(3, summary.Delivered);
        Assert.Equal(2, summary.Batches);
        var batches = await ledger.ListWorkBatchesAsync(Submission1, 10, 0);
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
        Assert.Equal(3, batches.Sum(b => b.Delivered));
        Assert.All(batches, b => Assert.Null(b.LeaseOwner));

        var delivered = await ledger.GetRecordAsync(flow.Id, records[0].Key);
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Null(delivered.PendingDocumentRef);
        Assert.Null(delivered.WorkBatch);
        Assert.Contains("\"recordId\":\"" + delivered.TargetId, delivered.TargetStateJson, StringComparison.Ordinal);
        Assert.Contains("\"version\"", delivered.TargetStateJson, StringComparison.Ordinal);

        var attempts = await ledger.ListAttemptsAsync(records[0].Key, 5);
        var attempt = Assert.Single(attempts);
        Assert.NotNull(attempt.WorkBatch);
        using var result = JsonDocument.Parse(attempt.ResultJson!);
        Assert.Equal("fake", result.RootElement.GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.Equal(200, result.RootElement.GetProperty("steps")[0].GetProperty("status").GetInt32());
        Assert.Equal(delivered.TargetId, result.RootElement.GetProperty("returned").GetProperty("recordId").GetString());

        var closed = await runtime.Intake.CompleteAsync(Submission1, flow.Id);
        Assert.Equal(SubmissionStatus.Completed, closed.Status);
        Assert.Equal(3, closed.Delivered);
    }

    [Fact]
    public async Task A_retry_resumes_after_the_steps_the_first_try_completed()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("resume", records, Submission1);
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = Samples.LocalFlow(drop);
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, new Dictionary<string, string> { ["logSource"] = "STAT_COMP" }, drop);
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
            throw new HttpStatusException(503, "HTTP 503 Service Unavailable");
        };

        await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, drop, force: false);
        var first = await Worker(runtime, protocol, ledger).DrainAsync(Submission1);
        Assert.Equal(3, first.Retried);
        var waiting = await ledger.GetRecordAsync(flow.Id, records[0].Key);
        Assert.Equal(RecordStatus.Pending, waiting!.Status);
        Assert.Contains("\"metadata\"", waiting.PendingStepJson, StringComparison.Ordinal);
        Assert.NotNull(await ledger.NextDueAsync(flow.Id, Submission1, _clock.GetUtcNow().UtcDateTime));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await Worker(runtime, protocol, ledger, "resumer").DrainAsync(Submission1);
        Assert.Equal(3, second.Delivered);
        Assert.Equal(3, resumed);
        var delivered = await ledger.GetRecordAsync(flow.Id, records[0].Key);
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Null(delivered.PendingStepJson);
        var attempts = await ledger.ListAttemptsAsync(records[0].Key, 5);
        Assert.Equal(2, attempts.Count);
        Assert.Contains("\"resumed\":true", attempts[0].ResultJson, StringComparison.Ordinal);
        Assert.Equal(AttemptOutcome.Failed, attempts[1].Outcome);
        Assert.Contains("\"metadata\"", attempts[1].ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deliver_run_fans_its_intake_and_drains_out_and_completes_the_submission()
    {
        var records = SampleDropBuilder.DefaultRecords("STAT_COMP");
        var drop = await DropAsync("fanout", records, Submission1, partitions: 2);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var local = Samples.LocalFlow(drop);
        var flow = local with { Reliability = local.Reliability with { FanOut = 2, FanOutMinRecords = 1, BatchRecords = 1 } };
        var dispatcher = new InlineDispatcher(protocol, flow);
        var engine = Samples.Engine(ledger, _clock) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, new Dictionary<string, string> { ["logSource"] = "STAT_COMP" }, drop);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);

        // Two partitions over the root and two members: the root took partition 0, one member took partition 1.
        Assert.Equal(1, result.IntakeMembers);
        Assert.Equal(2, result.DrainMembers);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(3, result.Submission.Planned);
        Assert.Equal(3, result.Submission.Delivered);
        Assert.Equal(3, result.Submission.BatchCount);
        Assert.Equal(2, result.Submission.Partitions);
        Assert.Equal(3, protocol.Deliveries.Count);

        var intakeMember = Assert.Single(dispatcher.Enqueued, e => e.Operation == RunParameters.IntakeOperation);
        Assert.Equal([1], intakeMember.Parameters.Partitions);
        Assert.Equal(Submission1, intakeMember.Parameters.SubmissionId);
        Assert.Equal(2, dispatcher.Enqueued.Count(e => e.Operation == RunParameters.DrainOperation));

        var batches = await ledger.ListWorkBatchesAsync(Submission1, 10, 0);
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
        // The member's batch numbers live in their partition's namespace, never colliding with the root's.
        Assert.Contains(batches, b => b.Index >= SubmissionIntakeNamespace(1));
        Assert.Contains(batches, b => b.Index < SubmissionIntakeNamespace(1));

        var activities = await ledger.ListActivitiesAsync(new ActivityQuery { SubmissionId = Submission1, Max = 20 });
        Assert.Contains(activities, a => a.Kind == "deliver" && a.Outcome == "completed");
        Assert.Contains(activities, a => a.Kind == "intake" && a.Outcome == "completed");
        Assert.Contains(activities, a => a.Kind == "drain" && a.Outcome == "completed");
    }

    private static int SubmissionIntakeNamespace(int partition) => Engine.Intake.SubmissionIntake.BatchBase(partition);

    /// <summary>A dispatcher that runs every member inline, on its own runtime over the same engine, as another node would.</summary>
    private sealed class InlineDispatcher : IFanOutDispatcher
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly FakeProtocol _protocol;
        private readonly FlowDefinition _flow;
        private readonly Dictionary<Guid, FanOutMemberState> _members = new();
        private readonly Dictionary<Guid, List<Guid>> _groups = new();

        public InlineDispatcher(FakeProtocol protocol, FlowDefinition flow)
        {
            _protocol = protocol;
            _flow = flow;
        }

        public EngineContext? Engine { get; set; }

        public List<(string Operation, RunParameters Parameters)> Enqueued { get; } = [];

        public bool Available => true;

        public async Task<FanOutHandle> EnqueueAsync(Guid rootRunId, string operation, IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        {
            var groupId = Guid.NewGuid();
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                Enqueued.Add((operation, member));
                var runId = Guid.NewGuid();
                ids.Add(runId);
                string resultJson;
                if (operation == RunParameters.IntakeOperation)
                {
                    using var runtime = await FlowRuntime.CreateAsync(Engine!, _flow, member.Values, member.Drop, ct);
                    runtime.RunId = runId;
                    var intake = await runtime.IntakeAsync(member.Force, member.Partitions, ct);
                    resultJson = JsonSerializer.Serialize(IntakeOutcome.From(intake, member.Drop!, member.Partitions), Json);
                }
                else
                {
                    using var runtime = FlowRuntime.ForTarget(Engine!, _flow);
                    runtime.RunId = runId;
                    var drained = await runtime.WorkAsync(once: false, member.SubmissionId, ct);
                    resultJson = JsonSerializer.Serialize(DrainOutcome.From(drained, member.SubmissionId), Json);
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
            // temp
        }
    }
}
