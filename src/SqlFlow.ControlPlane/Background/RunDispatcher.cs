using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Enqueues a flow run onto the durable queue and returns its newly minted id. The queue itself is the catalog's
/// <see cref="CatalogRun"/> table (see <see cref="RunQueueStore"/>), so a queued run survives a restart, is visible to
/// the read API immediately, and can be drained by more than one worker. This seam is where a future backend can
/// route a run to a remote pool instead of the in-process worker; the endpoint depends only on the interface.
/// </summary>
public interface IRunDispatcher
{
    /// <summary>Enqueues the run described by <paramref name="request"/> (references only - repo, flow, optional
    /// pool and commit SHA) using the caller's catalog context, and returns the minted run id so the caller can
    /// point a client at <c>GET /api/v1/runs/{runId}</c>.</summary>
    Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default);

    /// <summary>Cancels a run: a still-queued run is dequeued outright; a run already executing has a durable cancel
    /// request stamped for its owning node to honor. Returns the precise <see cref="CancelOutcome"/> so the endpoint
    /// can answer 200 / 202 / 404 / 409. Goes through the dispatcher (not the store directly) so the in-process
    /// backend can also nudge the local worker to observe the request without waiting out its poll interval.</summary>
    Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default);
}

/// <summary>
/// The in-process backend: it writes the queued run through <see cref="RunQueueStore"/> and then nudges the local
/// <see cref="RunExecutionWorker"/> via <see cref="RunQueueSignal"/> so a triggered run is picked up immediately
/// rather than waiting for the worker's next poll. Durability and ordering live in the database; the signal is only
/// a latency optimization, so a missed signal still means the run is drained on the next poll.
/// </summary>
public sealed class InProcessRunDispatcher : IRunDispatcher
{
    private readonly RunQueueSignal _signal;
    private readonly TimeProvider _clock;

    public InProcessRunDispatcher(RunQueueSignal signal, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(clock);
        _signal = signal;
        _clock = clock;
    }

    public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
    {
        var runId = await RunQueueStore
            .EnqueueAsync(catalog, request, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        _signal.Signal();
        return runId;
    }

    public async Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
    {
        var outcome = await RunQueueStore
            .CancelAsync(catalog, runId, _clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);

        // A running run's cancel is observed by the worker's poll: nudge it so the abort happens at once rather than
        // waiting out the poll interval. (A queued run is already gone; the worker has nothing to observe.) Like the
        // enqueue nudge this is only a latency optimization - a missed signal still means the next poll honors it.
        if (outcome == CancelOutcome.CancelRequested)
        {
            _signal.Signal();
        }

        return outcome;
    }
}

/// <summary>
/// A one-slot wake-up between the dispatcher (and scheduler) and the worker: signalling makes the worker's next
/// wait return at once so a freshly queued run is drained without waiting out the poll interval. Coalesces, so any
/// number of signals while the worker is busy collapse into a single immediate re-drain (which then drains the
/// whole queue). It is purely an optimization layered over polling, never the source of truth.
/// </summary>
public sealed class RunQueueSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    /// <summary>Wakes the worker if it is waiting, or marks that it should not wait next time. Idempotent while a
    /// wake is already pending.</summary>
    public void Signal()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; one re-drain handles any number of enqueues, so this is a no-op.
        }
    }

    /// <summary>Waits until signalled or until <paramref name="timeout"/> elapses (the poll fallback). The return
    /// value is irrelevant to the caller: the worker drains the queue on either path.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);

    public void Dispose() => _semaphore.Dispose();
}
