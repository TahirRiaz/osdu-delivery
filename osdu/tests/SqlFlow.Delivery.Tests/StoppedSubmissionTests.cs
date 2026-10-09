using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A submission its run left unsettled (osdu/docs/reference/concepts/submissions.md, Recovering a stopped submission): the
/// node or the process running it stopped part way, so the submission stays received, planned or running with records
/// still planned in its work batches. A later deliver run of the flow takes it over once no run holds it and no lease that
/// has not run out holds its work, sends each record once and closes it; a run the platform executes again resumes the
/// submission its interrupted attempt left rather than registering another. Over the module's database on SQL Server, the
/// sample estate in the in-memory ingestion tables and a fake protocol.
/// </summary>
[Collection(SqlServerSuite.Name)]
public sealed class StoppedSubmissionTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static int LogCount => SampleEstate.Logs().Count;

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.Actor = "schedule:welldb";
        return (runtime, protocol, ledger);
    }

    /// <summary>
    /// The run that stops: it plans every record into its submission, and the worker that takes the first batch stops
    /// before sending anything, its lease left to run out. Its process ended, so it holds the submission no longer.
    /// </summary>
    private async Task<Guid> PlanAndStopAsync(FlowRuntime runtime, OsduLedger ledger, Guid run)
    {
        runtime.RunId = run;
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        Assert.Equal(SubmissionStatus.Planned, intake.Submission.Status);
        Assert.NotNull(await ledger.ClaimWorkBatchAsync(runtime.Flow.Id, intake.Submission.SubmissionId, "stopped-node", Lease, Now));
        return intake.Submission.SubmissionId;
    }

    private static void AssertEachSentOnce(FakeProtocol protocol)
    {
        Assert.Equal(LogCount, protocol.Deliveries.Count);
        Assert.Equal(LogCount, protocol.Deliveries.Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public async Task A_later_run_takes_over_a_submission_its_stopped_run_left_and_sends_each_record_once()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            var stopped = await PlanAndStopAsync(runtime, ledger, Guid.CreateVersion7());
            _clock.Advance(Lease * 2);

            // The flow's next scheduled run plans nothing new: every row is what its record already queues in the stopped
            // submission. It still sends what that submission holds, as the stopped run would have, and closes it.
            var later = Guid.CreateVersion7();
            runtime.RunId = later;
            var run = await runtime.RunAsync(force: false);

            AssertEachSentOnce(protocol);
            Assert.NotEqual(stopped, run.Submission.SubmissionId);
            Assert.Equal(LogCount, run.Work.Delivered);
            var closed = (await ledger.GetSubmissionAsync(stopped))!;
            Assert.Equal((SubmissionStatus.Completed, (long)LogCount, (string?)null), (closed.Status, closed.Delivered, closed.Error));
            Assert.NotNull(closed.CompletedUtc);
            Assert.Equal(0, await ledger.CountWorkBatchesAsync(stopped, WorkBatchStatus.Queued));
            Assert.Null(await ledger.NextLeaseExpiryAsync(runtime.Flow.Id, stopped));

            // Every record's history says which submission it was sent for and which run sent it.
            for (var i = 0; i < LogCount; i++)
            {
                var record = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(i)))!;
                Assert.Equal(RecordStatus.Delivered, record.Status);
                var delivered = Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(i), 10), a => a.Outcome == AttemptOutcome.Delivered);
                Assert.Equal((stopped, (Guid?)later), (delivered.SubmissionId!.Value, delivered.RunId));
            }

            // Taken over and closed once: the run after it finds nothing left to take.
            protocol.Deliveries.Clear();
            runtime.RunId = Guid.CreateVersion7();
            var next = await runtime.RunAsync(force: false);
            Assert.Empty(protocol.Deliveries);
            Assert.True(next.Idle);
        }
    }

    [Fact]
    public async Task A_submission_a_run_still_holds_or_a_live_lease_works_on_is_left_alone_until_neither_does()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            runtime.RunId = Guid.CreateVersion7();
            var planned = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            var submissionId = planned.Submission.SubmissionId;

            // The run that planned it still works on it, on another node: it holds the submission, and a later run of the flow
            // neither sends its records nor closes it.
            var alive = await ledger.HoldSubmissionAsync(submissionId);
            runtime.RunId = Guid.CreateVersion7();
            await runtime.RunAsync(force: false);
            Assert.Empty(protocol.Deliveries);
            Assert.Equal(SubmissionStatus.Planned, (await ledger.GetSubmissionAsync(submissionId))!.Status);

            // That run has ended, but a worker that holds no submission (a drain of the whole flow) is sending its batch
            // under a lease that has not run out: the submission is still left to that worker.
            await alive.DisposeAsync();
            var claim = await ledger.ClaimWorkBatchAsync(runtime.Flow.Id, null, "whole-flow-drain", Lease, Now);
            Assert.Equal(submissionId, claim!.Batch.SubmissionId);
            runtime.RunId = Guid.CreateVersion7();
            await runtime.RunAsync(force: false);
            Assert.Empty(protocol.Deliveries);
            Assert.Equal(SubmissionStatus.Planned, (await ledger.GetSubmissionAsync(submissionId))!.Status);
            Assert.True(await ledger.IsSubmissionLeasedAsync(runtime.Flow.Id, submissionId, Now));

            // The worker stopped too, and its lease has run out: nobody works on the submission, and the next run sends it.
            _clock.Advance(Lease * 2);
            Assert.False(await ledger.IsSubmissionLeasedAsync(runtime.Flow.Id, submissionId, Now));
            runtime.RunId = Guid.CreateVersion7();
            await runtime.RunAsync(force: false);
            AssertEachSentOnce(protocol);
            Assert.Equal(SubmissionStatus.Completed, (await ledger.GetSubmissionAsync(submissionId))!.Status);
        }
    }

    [Fact]
    public async Task A_submission_whose_planning_never_finished_is_sent_as_far_as_it_got_and_closed_failed_with_why()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            // The run stopped while it planned: its records are staged in work batches, but the submission was never finalised.
            var stoppedRun = Guid.CreateVersion7();
            runtime.RunId = stoppedRun;
            var prepared = await runtime.Intake.PrepareAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            var counts = await runtime.Intake.PlanSlicesAsync(runtime.Flow, prepared, null);
            Assert.Equal(LogCount, counts.Planned);
            await runtime.HeldSubmissions.ReleaseAllAsync();
            var stopped = prepared.Submission.SubmissionId;
            Assert.Equal(SubmissionStatus.Received, (await ledger.GetSubmissionAsync(stopped))!.Status);

            var later = Guid.CreateVersion7();
            runtime.RunId = later;
            await runtime.RunAsync(force: false);

            AssertEachSentOnce(protocol);
            var closed = (await ledger.GetSubmissionAsync(stopped))!;
            Assert.Equal((SubmissionStatus.Failed, (long)LogCount), (closed.Status, closed.Delivered));
            Assert.Contains($"run {stoppedRun:D} ended before its planning finished", closed.Error, StringComparison.Ordinal);
            Assert.Contains($"run {later:D} took it over", closed.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_run_executed_again_after_an_interruption_resumes_the_submission_its_attempt_left()
    {
        var (runtime, protocol, ledger) = await RuntimeAsync();
        using (runtime)
        {
            // The platform executes the same run again after the node running it was lost.
            var run = Guid.CreateVersion7();
            var interrupted = await PlanAndStopAsync(runtime, ledger, run);
            _clock.Advance(Lease * 2);

            runtime.RunId = run;
            var resumed = await runtime.RunAsync(force: false);

            // One submission for the run: the one its interrupted attempt registered, planned again and sent whole.
            Assert.Equal(interrupted, resumed.Submission.SubmissionId);
            Assert.Equal(SubmissionStatus.Completed, resumed.Submission.Status);
            Assert.Equal(LogCount, resumed.Submission.Delivered);
            var submission = Assert.Single(await ledger.ListSubmissionsAsync(runtime.Flow.Id, 10));
            Assert.Equal((interrupted, (Guid?)run), (submission.SubmissionId, submission.RunId));
            AssertEachSentOnce(protocol);
        }
    }

    [Fact]
    public async Task A_key_scoped_attempt_resumes_only_the_submission_made_for_the_same_records()
    {
        var (runtime, _, ledger) = await RuntimeAsync();
        using (runtime)
        {
            // Another run planned the scope, so the records are in the ledger with the key tuples they are read by.
            runtime.RunId = Guid.CreateVersion7();
            await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);

            // An attempt of a run plans one record by key (a pass over the records asked to be planned again) and stops while
            // it plans; another attempt of the same run asks for another record, and registers a submission of its own.
            runtime.RunId = Guid.CreateVersion7();
            var first = await KeysIntakeAsync(0);
            var other = await KeysIntakeAsync(1);
            Assert.NotEqual(first, other);

            // Asked for the same record again, the run resumes the submission made for it, which is still not settled.
            Assert.Equal(first, await KeysIntakeAsync(0));
            Assert.Equal(3, (await ledger.ListSubmissionsAsync(runtime.Flow.Id, 10)).Count);
        }

        async Task<Guid> KeysIntakeAsync(int index)
        {
            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(index)))!;
            runtime.Selection = SourceSelection.ForKeys([KeyTuple.FromJson(record.SourceKeyJson!)]);
            var prepared = await runtime.Intake.PrepareAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: true);
            await runtime.HeldSubmissions.ReleaseAllAsync();
            return prepared.Submission.SubmissionId;
        }
    }

    [Fact]
    public async Task A_hold_is_shared_by_the_runs_working_on_a_submission_and_a_take_over_waits_until_none_holds_it()
    {
        var ledger = _db.Ledger(_clock);
        var submissionId = Guid.CreateVersion7();

        // A fan-out's coordinator and a member both hold it: no take-over while either does.
        var coordinator = await ledger.HoldSubmissionAsync(submissionId);
        var member = await ledger.HoldSubmissionAsync(submissionId);
        Assert.Null(await ledger.TakeOverSubmissionAsync(submissionId));
        await coordinator.DisposeAsync();
        Assert.Null(await ledger.TakeOverSubmissionAsync(submissionId));

        // A runtime disposed without awaiting lets go too.
        member.Dispose();

        // Nobody holds it now: one take-over gets it, a second does not while the first holds it, and a run naming the
        // submission meanwhile is not kept waiting.
        var taken = await ledger.TakeOverSubmissionAsync(submissionId);
        Assert.NotNull(taken);
        Assert.Equal(submissionId, taken.SubmissionId);
        Assert.Null(await ledger.TakeOverSubmissionAsync(submissionId));
        var naming = await ledger.HoldSubmissionAsync(submissionId).WaitAsync(TimeSpan.FromSeconds(10));
        await naming.DisposeAsync();
        await taken.DisposeAsync();
        await taken.DisposeAsync();

        var again = await ledger.TakeOverSubmissionAsync(submissionId);
        Assert.NotNull(again);
        await again.DisposeAsync();
    }

    public void Dispose()
    {
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
