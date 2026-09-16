using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The worker's journal: what its concurrent deliveries hand over while a write is in flight goes into the next write,
/// each caller hears back once its entry is stored, the listener hears of a try only after its attempt is stored, and a
/// write that fails fails exactly the entries it carried.
/// </summary>
public sealed class LeaseJournalTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly SqliteOsdu _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("journal-flow");
    private readonly Guid _submission = Guid.NewGuid();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Entries_that_arrive_while_a_write_is_in_flight_are_written_together_and_each_caller_hears_once_its_entry_is_stored()
    {
        var (ledger, claim) = await ClaimAsync(5);
        var gated = GatedLedger.Over(ledger);
        var listener = new RecordingListener();
        var journal = new LeaseJournal(gated.Ledger, _flow, claim.Lease!.Token, listener);
        var records = claim.Records;

        var first = journal.OutcomeAsync(Delivered(records[0]), Reported(records[0]));
        await gated.FirstWriteStarted.WaitAsync(Patience);
        var step = journal.StepAsync(new RecordStep(records[1].DeliveryKey, _submission, records[1].PendingDocumentRef!, "{\"upload\":{\"n\":\"1\"}}", Now));
        var rest = records.Skip(1).Select(r => journal.OutcomeAsync(Delivered(r), Reported(r))).ToList();

        // Nothing is acknowledged, and nothing reported, while the first write is held.
        Assert.False(first.IsCompleted);
        Assert.False(step.IsCompleted);
        Assert.All(rest, t => Assert.False(t.IsCompleted));
        Assert.Empty(listener.Events);

        gated.Release();
        await Task.WhenAll([first, step, .. rest]).WaitAsync(Patience);

        // Two writes: the one in flight, then everything that queued behind it.
        Assert.Equal([(0, 1), (1, 4)], gated.Writes.Select(w => (w.Steps.Count, w.Completions.Count)));
        Assert.Equal(records.Select(r => (DeliveryKey?)r.DeliveryKey), listener.Events.Select(e => e.DeliveryKey));
        var (byStatus, unchanged) = journal.Written;
        Assert.Equal((5L, 0L), (byStatus[RecordStatus.Delivered], unchanged));
        Assert.Equal(5, await ledger.CountAttemptsAsync(_submission, AttemptOutcome.Delivered));

        // The records change when the lease applies what was written.
        Assert.Equal(RecordStatus.Delivering, (await ledger.GetRecordAsync(_flow, records[4].DeliveryKey))!.Status);
        Assert.Equal(new LeaseApplied(5, 0), await ledger.CheckpointLeaseAsync(claim.Lease.Token, Now));
        Assert.Equal(5, await ledger.CountAsync(_flow, _submission, RecordStatus.Delivered));
    }

    [Fact]
    public async Task A_write_that_fails_fails_the_entries_it_carried_and_the_journal_goes_on_writing()
    {
        var (ledger, claim) = await ClaimAsync(2);
        var gated = GatedLedger.Over(ledger);
        var listener = new RecordingListener();
        var journal = new LeaseJournal(gated.Ledger, _flow, claim.Lease!.Token, listener);
        var records = claim.Records;

        var first = journal.OutcomeAsync(Delivered(records[0]), Reported(records[0]));
        await gated.FirstWriteStarted.WaitAsync(Patience);

        // A try of a record the ledger does not hold shares the next write with a try that is fine; the ledger refuses
        // the write as a whole, so both are refused and neither is reported.
        var stranger = Delivered(records[1]) with { DeliveryKey = DeliveryKey.Derive("journal", ["not-staged"]) };
        stranger = stranger with { Attempt = stranger.Attempt with { DeliveryKey = stranger.DeliveryKey } };
        var refused = journal.OutcomeAsync(stranger, Reported(records[1]));
        var sharing = journal.OutcomeAsync(Delivered(records[1]), Reported(records[1]));
        gated.Release();

        await first.WaitAsync(Patience);
        var error = await Assert.ThrowsAsync<DeliveryException>(() => refused.WaitAsync(Patience));
        Assert.Contains("is not in the ledger of flow", error.Message, StringComparison.Ordinal);
        Assert.Same(error, await Assert.ThrowsAsync<DeliveryException>(() => sharing.WaitAsync(Patience)));
        Assert.Single(listener.Events);
        Assert.Equal(1, await ledger.CountAttemptsAsync(_submission, AttemptOutcome.Delivered));

        // The journal starts a new write for the next entry.
        await journal.OutcomeAsync(Delivered(records[1]), Reported(records[1])).WaitAsync(Patience);
        Assert.Equal(2, listener.Events.Count);
        Assert.Equal(2L, journal.Written.ByStatus[RecordStatus.Delivered]);
        Assert.Equal(new LeaseApplied(2, 0), await ledger.CheckpointLeaseAsync(claim.Lease.Token, Now));
    }

    public void Dispose() => _db.Dispose();

    private async Task<(OsduLedger Ledger, ClaimedRecords Claim)> ClaimAsync(int count)
    {
        var ledger = _db.Ledger(_clock);
        await ledger.UpsertPendingAsync(_flow, Enumerable.Range(0, count).Select(i => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("journal", [$"j-{i}"]),
            FlowId = _flow,
            SourceKey = $"j-{i}",
            MappingName = "Thing",
            TargetId = $"dev:x:j-{i}",
            LastSubmissionId = _submission,
            PendingDocumentRef = $"0:{i * 10}:10",
            PendingRenderContext = "{}",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
        }).ToList());
        var claim = await ledger.ClaimAsync(_flow, _submission, "w1", count, TimeSpan.FromMinutes(5), Now);
        Assert.Equal(count, claim.Records.Count);
        return (ledger, claim with { Records = [.. claim.Records.OrderBy(r => r.SourceKey, StringComparer.Ordinal)] });
    }

    private RecordCompletion Delivered(RecordState claimed) => new()
    {
        DeliveryKey = claimed.DeliveryKey,
        Status = RecordStatus.Delivered,
        Promote = true,
        TargetVersion = 1,
        Claimed = ClaimedWork.Of(claimed),
        Attempt = new AttemptRecord
        {
            DeliveryKey = claimed.DeliveryKey, SubmissionId = _submission, Worker = "w1", StartedUtc = Now, CompletedUtc = Now,
            Outcome = AttemptOutcome.Delivered, Phase = "metadata", TargetVersion = 1,
        },
    };

    private DeliveryEvent Reported(RecordState claimed) => new()
    {
        AtUtc = Now,
        FlowId = _flow,
        FlowName = "journal-flow",
        Kind = "record.delivered",
        SubmissionId = _submission,
        DeliveryKey = claimed.DeliveryKey,
        SourceKey = claimed.SourceKey,
    };
}

/// <summary>A ledger whose first append waits until the test lets it go, and which keeps what every append carried.</summary>
public class GatedLedger : DispatchProxy
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<LeaseAppend> _writes = new();
    private ILedger? _inner;
    private int _appends;

    /// <summary>Completes when the first append reaches the ledger.</summary>
    public Task FirstWriteStarted => _started.Task;

    /// <summary>What each append carried, in the order they reached the ledger.</summary>
    public IReadOnlyList<LeaseAppend> Writes => [.. _writes];

    /// <summary>The proxy, as the ledger the code under test takes.</summary>
    public ILedger Ledger => (ILedger)(object)this;

    public static GatedLedger Over(ILedger inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var gated = (GatedLedger)(object)Create<ILedger, GatedLedger>();
        gated._inner = inner;
        return gated;
    }

    /// <summary>Lets the first append go on.</summary>
    public void Release() => _released.TrySetResult();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var inner = _inner ?? throw new InvalidOperationException("The gated ledger was made without the ledger it wraps.");
        if (targetMethod.Name == nameof(ILedger.AppendAsync))
        {
            return AppendAsync(inner, (Guid)args![0]!, (string)args[1]!, (LeaseAppend)args[2]!, (CancellationToken)args[3]!);
        }

        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    private async Task AppendAsync(ILedger inner, Guid flowId, string token, LeaseAppend append, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _appends) == 1)
        {
            _started.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        _writes.Enqueue(append);
        await inner.AppendAsync(flowId, token, append, ct);
    }
}
