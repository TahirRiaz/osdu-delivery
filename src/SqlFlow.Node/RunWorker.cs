using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.Node;

/// <summary>
/// The compute-node runtime: it drains the durable run queue by atomically claiming the oldest queued runs (so two
/// nodes never run the same flow) and executing up to a bounded number of them concurrently, each on its own DI
/// scope, resolving the flow file from the catalog (repo root + relative path), executing it through the shared
/// <see cref="DocumentExecutor"/> (identical to a CLI run), and recording the outcome from the produced artifact
/// under the run id the trigger already returned. The queue is the database, so the runtime is stateless and
/// horizontally scalable: the control plane hosts it in-process, and a self-hosted <c>worker</c> hosts the very
/// same loop on a node inside a private network. Each node resolves every credential from its own environment,
/// so nothing sensitive travels through the queue.
/// </summary>
/// <remarks>
/// Robustness: one run's failure never tears down the loop or its sibling runs (per-run try/catch drives the run
/// to a terminal state and continues); a poll/claim error (a transient database outage) is logged and retried next
/// tick. On startup it requeues any run this node left <c>running</c> (an orphan from a previous incarnation that
/// stopped mid-run). On shutdown it stops claiming and lets the in-flight runs finish, or, if cancelled, leaves
/// them <c>running</c> for the next start to recover. All diagnostics are logged with secret-redacted messages.
/// </remarks>
public sealed partial class RunWorker
{
    /// <summary>The default bound on how many claimed runs a node executes at once (see <see cref="RunAsync"/>).</summary>
    public const int DefaultMaxConcurrentRuns = 4;

    /// <summary>The default bound on how many claimed COMPUTE TASKS a node executes at once. Deliberately its
    /// own small gate, separate from the run gate: compute tasks are interactive (an operator probing a target
    /// from the GUI), so a node saturated with long runs must still answer them promptly, and a burst of them
    /// must never starve run execution of its slots.</summary>
    public const int DefaultMaxConcurrentComputeTasks = 2;

    /// <summary>How often the node refreshes its fleet heartbeat, on a cadence independent of the drain loop (see
    /// <see cref="HeartbeatLoopAsync"/>). Well under the control plane's 60s online window, so a node stays visibly
    /// online across several beats even if one is missed, without heartbeating so often it is noise.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long a stopping node keeps executing the work it has ALREADY claimed before severing it. A stop
    /// is routine and frequent in an autoscaled fleet - the scaler reclaims a replica, a revision swaps, an operator
    /// restarts a node - and it means "stop claiming and finish what you hold", never "drop it". Severing a run
    /// records no outcome at all, so it is recovered only by the reaper's requeue, which consumes one of the run's
    /// <see cref="RunQueueStore.MaxExecutionAttempts"/> executions and repeats all of its work; a run unlucky enough
    /// to be caught by three stops is then failed outright and blamed for dying, though nothing was ever wrong with
    /// it. Draining is what keeps a scale-in from manufacturing those failures. The default sits under the ten-minute
    /// termination grace the worker's container app declares, so the drain ends on the node's own terms with an
    /// outcome recorded, rather than being cut off mid-flight by the platform's kill.</summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromMinutes(9);

    /// <summary>How often the drain re-checks its in-flight work, and re-polls operator cancels, while it waits.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly DocumentExecutor _executor;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunWorker> _logger;
    private readonly string _node;
    private readonly string? _version = typeof(RunWorker).Assembly.GetName().Version?.ToString();
    private readonly GitMaterializer _materializer = new();

    // The pool this node serves, stamped on its heartbeat so the fleet view can count workers online per pool. Empty
    // string for an untargeted node (the default pool); the pool name for a pooled node. Set once in RunAsync.
    private string _pool = string.Empty;

    // The moment this incarnation started draining, stamped once in RunAsync. An operator restart request is honored
    // only when it is newer than this, so a stale request left on the node row by a previous incarnation never
    // bounces this one and a same-name restart can never loop.
    private DateTime _startedUtc;

    // Invoked once when this node observes a fresh restart request: the host wires it to a graceful stop (drain, then
    // exit) so the orchestrator recreates the replica. Null in a caller that does not support restart (the observe
    // path then does nothing).
    private Func<CancellationToken, Task>? _onRestartRequested;

    // Set the first time a restart is initiated so the stop fires exactly once, even across the several heartbeats
    // that may elapse while the drain completes.
    private int _restartInitiated;

    // The runs this node is currently executing, keyed by run id, each with its own cancellation source linked to
    // the shutdown token. An operator cancel of a running run trips its source (see PollCancellationsAsync), which
    // aborts the in-flight work; ExecuteClaimedAsync registers a run here before it starts and removes it when it
    // ends. Concurrent because the drain loop registers while executing tasks remove.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    // The compute tasks this node is currently executing, with the same per-item cancellation discipline as
    // _running. A separate map because run ids and task ids come from different tables and are cancelled through
    // different store calls.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningTasks = new();

    /// <summary>
    /// A node draining the run queue under <paramref name="identity"/>, or, when none is given, under the name
    /// <see cref="NodeIdentity.Resolve"/> finds (<c>SQLFLOW_NODE_NAME</c>, else the machine name).
    /// </summary>
    public RunWorker(IServiceProvider services, DocumentExecutor executor, TimeProvider clock, ILogger<RunWorker> logger, NodeIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _executor = executor;
        _clock = clock;
        _logger = logger;
        _node = (identity ?? NodeIdentity.Resolve(null)).Name;
    }

    /// <summary>This node's identity (its configured name, else its machine name), stamped on a claimed run so its
    /// work is attributable and recoverable.</summary>
    public string NodeName => _node;

    /// <summary>
    /// Runs the drain loop until <paramref name="stoppingToken"/> is cancelled: recover this node's orphans, then
    /// repeatedly drain the queue and wait. <paramref name="waitForWork"/> is how the loop idles between drains - the
    /// control plane passes its in-process nudge (so a triggered run starts within milliseconds), a standalone
    /// worker passes a plain delay - and <paramref name="pollInterval"/> bounds that wait so schedule- and
    /// other-node-enqueued runs (and recovery) are still picked up. <paramref name="pools"/> are the pools this node
    /// serves: it claims untargeted runs plus runs routed to one of these (an empty list = untargeted only).
    /// <paramref name="maxConcurrentRuns"/> bounds how many claimed runs execute at once on this node (minimum 1):
    /// the queue's atomic claim already supports concurrent claimants, so the bound only sizes this node's own
    /// in-flight work, and a saturated node stops claiming so queued runs stay available to other nodes.
    /// <para><paramref name="stoppingToken"/> stops this node CLAIMING; it does not sever what the node is already
    /// executing. Once it trips, the in-flight runs keep going (and the node keeps heartbeating, so the reaper does
    /// not mistake a draining node for a dead one and requeue the very work it is finishing) for up to
    /// <paramref name="drainTimeout"/>, defaulting to <see cref="DefaultDrainTimeout"/>. Only when that window
    /// expires are the survivors cancelled and left <c>running</c> for recovery. Pass <see cref="TimeSpan.Zero"/> to
    /// sever immediately.</para>
    /// </summary>
    public async Task RunAsync(
        TimeSpan pollInterval, IReadOnlyList<string> pools, Func<TimeSpan, CancellationToken, Task> waitForWork,
        CancellationToken stoppingToken, int maxConcurrentRuns = DefaultMaxConcurrentRuns,
        int maxConcurrentComputeTasks = DefaultMaxConcurrentComputeTasks,
        Func<CancellationToken, Task>? onRestartRequested = null,
        TimeSpan? drainTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(waitForWork);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentComputeTasks, 1);
        var drain = drainTimeout ?? DefaultDrainTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThan(drain, TimeSpan.Zero);

        _startedUtc = _clock.GetUtcNow().UtcDateTime;
        _onRestartRequested = onRestartRequested;
        // A node serves at most one pool in the one-app-per-pool topology; an empty pools list is the default
        // (untargeted) pool. Stamped on every heartbeat so the fleet view attributes the node to its pool.
        _pool = pools.Count > 0 ? pools[0] : string.Empty;

        await RecoverOrphansAsync(stoppingToken).ConfigureAwait(false);

        // The abort token for work this node has already claimed. Deliberately NOT linked to stoppingToken: a stop
        // means "stop claiming", and the run keeps executing so it can record its own outcome. This source trips
        // only when the drain window below expires (or at once when the caller granted no window), which is the one
        // case where a run is abandoned mid-flight for the reaper to requeue.
        using var abortCts = new CancellationTokenSource();

        // The fleet heartbeat runs on its own cadence, decoupled from draining. A fully-saturated node blocks inside
        // DrainAsync waiting for a concurrency slot and never returns to the top of this loop, so a heartbeat welded
        // to the loop would stall for the whole of a long run and the node would falsely age out to "offline" while
        // healthy and busy. Running it as an independent task keeps a node's liveness truthful under any load, which
        // is also what lets the control-plane orphan reaper safely tell a dead node from a merely busy one. It
        // observes the ABORT token, not stoppingToken, so a node that is draining keeps beating: a draining node is
        // very much alive, and going silent for the drain would invite the reaper to requeue (and a sibling node to
        // re-execute) the runs it is in the middle of finishing. It never throws (HeartbeatAsync is best-effort) and
        // is awaited after the drain below.
        var heartbeat = HeartbeatLoopAsync(abortCts.Token);

        using var gate = new SemaphoreSlim(maxConcurrentRuns, maxConcurrentRuns);
        using var computeGate = new SemaphoreSlim(maxConcurrentComputeTasks, maxConcurrentComputeTasks);
        var inFlight = new ConcurrentDictionary<Guid, Task>();
        var computeInFlight = new ConcurrentDictionary<Guid, Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollCancellationsAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                // Compute tasks drain FIRST: they are interactive (an operator waiting in the GUI) and bounded by
                // their own gate, so serving them ahead of the run queue costs run throughput nothing.
                await DrainComputeAsync(pools, computeGate, computeInFlight, stoppingToken, abortCts.Token).ConfigureAwait(false);
                await DrainAsync(pools, gate, inFlight, stoppingToken, abortCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A poll/claim error (a transient database outage) must not end the worker; log and try next tick.
                LogPollError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await waitForWork(pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        // Shutdown: claiming has stopped; now DRAIN the work this node already holds so each run records its own
        // outcome instead of being abandoned. The tasks never fault (the execute wrappers catch everything), so
        // this cannot throw, and it keeps both gates alive until every slot is released.
        var pending = inFlight.Values.Concat(computeInFlight.Values).ToArray();
        if (pending.Length > 0)
        {
            await DrainInFlightAsync(pending, drain, abortCts).ConfigureAwait(false);
        }

        // Every run is finished (or severed): stop the heartbeat and join it, so the node stops advertising itself
        // as alive the moment it stops holding work, and no background task outlives the worker.
        await abortCts.CancelAsync().ConfigureAwait(false);
        await heartbeat.ConfigureAwait(false);
    }

    /// <summary>
    /// Waits out the work this node already claimed after claiming has stopped, so a routine stop (an autoscaler
    /// reclaiming the replica, a revision swap, an operator restart) costs no run its progress: each in-flight run
    /// finishes and records its own outcome. The node keeps heartbeating throughout, so the orphan reaper leaves the
    /// draining work alone rather than requeueing runs that are about to complete.
    /// <para>Bounded by <paramref name="drainTimeout"/>: a node cannot drain forever, because the platform that asked
    /// it to stop will eventually kill it outright, and a run severed by that kill is strictly worse off than one
    /// cancelled here (the same requeue, minus the log line saying why). When the window expires the survivors are
    /// cancelled through <paramref name="abort"/> and left <c>running</c> for recovery. Operator cancels are still
    /// polled while waiting, so a drain can never trap a run an operator has asked to kill.</para>
    /// </summary>
    /// <param name="pending">The in-flight run and compute-task executions to wait on. They never fault (their
    /// execute wrappers catch everything), so this never throws.</param>
    /// <param name="drainTimeout">How long to keep waiting; <see cref="TimeSpan.Zero"/> severs at once.</param>
    /// <param name="abort">The source every in-flight execution runs under, tripped when the window expires.</param>
    /// <remarks>Internal for the test suite; only <see cref="RunAsync"/> calls it in production.</remarks>
    internal async Task DrainInFlightAsync(Task[] pending, TimeSpan drainTimeout, CancellationTokenSource abort)
    {
        var all = Task.WhenAll(pending);
        if (drainTimeout <= TimeSpan.Zero)
        {
            await abort.CancelAsync().ConfigureAwait(false);
            await all.ConfigureAwait(false);
            return;
        }

        LogDraining(pending.Length, (int)drainTimeout.TotalSeconds);
        var deadline = _clock.GetUtcNow() + drainTimeout;
        while (!all.IsCompleted)
        {
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                LogDrainTimedOut((int)drainTimeout.TotalSeconds);
                await abort.CancelAsync().ConfigureAwait(false);
                break;
            }

            // CancellationToken.None on the delay: the whole point of the drain is that it outlives the stop signal,
            // so nothing here may abandon the wait early.
            var slice = remaining < DrainPollInterval ? remaining : DrainPollInterval;
            await Task.WhenAny(all, Task.Delay(slice, CancellationToken.None)).ConfigureAwait(false);
            await PollCancellationsAsync(abort.Token).ConfigureAwait(false);
        }

        await all.ConfigureAwait(false);
        LogDrained();
    }

    /// <summary>Refreshes the fleet heartbeat on <see cref="HeartbeatInterval"/>, independent of the drain loop, until
    /// shutdown. Beats once immediately so a freshly started node is visible at once, then on the interval. Never
    /// throws: <see cref="HeartbeatAsync"/> is best-effort (a transient catalog error is logged and retried next
    /// beat), and the inter-beat delay ends quietly on shutdown.</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var restartRequestedUtc = await HeartbeatAsync(ct).ConfigureAwait(false);
            await MaybeHonorRestartAsync(restartRequestedUtc, ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Beats once and returns the node's pending restart request (null when none), so the loop can honor it
    /// on the same cadence. Best-effort: a transient catalog error is logged and treated as "no request" for this
    /// beat.</summary>
    private async Task<DateTime?> HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            // The beat carries how many runs this node is executing right now: the autoscaler's scale-in signal
            // (a busy node holds its replica; only idle ones are surplus). Count from the live registration map,
            // which is exact: runs register before execution starts and deregister when they reach an end state.
            return await NodeStore.HeartbeatAsync(
                catalog, _node, _version, _clock.GetUtcNow().UtcDateTime, _pool, _running.Count, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null; // Shutting down; nothing to do.
        }
        catch (Exception ex)
        {
            // The fleet heartbeat is best-effort: a failure must never affect draining; the next poll retries it.
            LogPollError(SecretHygiene.RedactedMessage(ex));
            return null;
        }
    }

    /// <summary>Honors an operator's restart request observed on the heartbeat: when the request is newer than this
    /// incarnation's start (so a stale request never bounces the replacement and a same-name restart cannot loop) and
    /// a stop has not already begun, logs and invokes the host's graceful-stop callback exactly once. The callback
    /// drives the same shutdown a SIGTERM would, so the drain loop finishes its in-flight work and exits, after which
    /// the orchestrator recreates the replica. A caller that supplied no callback simply never restarts.</summary>
    private async Task MaybeHonorRestartAsync(DateTime? requestedUtc, CancellationToken ct)
    {
        if (requestedUtc is not { } requested || requested <= _startedUtc || _onRestartRequested is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _restartInitiated, 1) != 0)
        {
            return; // already initiated on an earlier beat
        }

        LogRestartRequested(requested);
        try
        {
            await _onRestartRequested(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The callback drove the shutdown it was meant to; nothing more to do.
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <summary>Observes operator cancel requests for the runs this node is executing and trips each matching run's
    /// cancellation token, which aborts its in-flight work and drives it to <c>cancelled</c>. Best-effort like the
    /// heartbeat: it only queries when this node has in-flight runs, and a transient catalog error is logged and
    /// retried on the next poll rather than stopping the loop.</summary>
    private async Task PollCancellationsAsync(CancellationToken ct)
    {
        if (_running.IsEmpty && _runningTasks.IsEmpty)
        {
            return;
        }

        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            if (!_running.IsEmpty)
            {
                var requested = await RunQueueStore.ListCancelRequestedAsync(catalog, _node, ct).ConfigureAwait(false);
                foreach (var runId in requested)
                {
                    if (_running.TryGetValue(runId, out var cts) && !cts.IsCancellationRequested)
                    {
                        LogCancelling(runId);
                        cts.Cancel();
                    }
                }
            }

            if (!_runningTasks.IsEmpty)
            {
                var requested = await ComputeTaskStore.ListCancelRequestedAsync(catalog, _node, ct).ConfigureAwait(false);
                foreach (var taskId in requested)
                {
                    if (_runningTasks.TryGetValue(taskId, out var cts) && !cts.IsCancellationRequested)
                    {
                        LogTaskCancelling(taskId);
                        cts.Cancel();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; nothing to do.
        }
        catch (Exception ex)
        {
            // Observing cancels is best-effort: a failure must never affect draining; the next poll retries it.
            LogPollError(SecretHygiene.RedactedMessage(ex));
        }
    }

    private async Task RecoverOrphansAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var recovered = await RunQueueStore.RecoverStuckRunningAsync(catalog, _node, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (recovered > 0)
            {
                LogRecovered(recovered);
            }

            var recoveredTasks = await ComputeTaskStore.RecoverStuckRunningAsync(catalog, _node, ct).ConfigureAwait(false);
            if (recoveredTasks > 0)
            {
                LogRecoveredTasks(recoveredTasks);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down during startup recovery; nothing to do.
        }
        catch (ObjectDisposedException)
        {
            // Host teardown disposed the DI container and the loggers before startup recovery finished. Bail quietly:
            // logging would itself throw (the disposed Windows EventLog provider) and there is nothing left to recover.
        }
        catch (Exception ex)
        {
            // Recovery is best-effort: if the catalog is briefly unreachable at startup, the next poll retries the
            // claim path anyway. Do not let it stop the worker from starting.
            LogPollError(SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <param name="pools">The pools this node serves, alongside untargeted runs.</param>
    /// <param name="gate">The node's run-concurrency gate; a slot is held before each claim.</param>
    /// <param name="inFlight">The executing runs, for the shutdown drain to wait on.</param>
    /// <param name="ct">Stops CLAIMING: a tripped token ends this loop at once, leaving queued runs for other nodes.</param>
    /// <param name="abortCt">The token the claimed run executes under, tripped only when the drain window expires.</param>
    private async Task DrainAsync(
        IReadOnlyList<string> pools, SemaphoreSlim gate, ConcurrentDictionary<Guid, Task> inFlight, CancellationToken ct,
        CancellationToken abortCt)
    {
        while (!ct.IsCancellationRequested)
        {
            // A concurrency slot must be free before claiming: a claim flips the run to 'running', so a node must
            // never claim more than it can execute (a saturated node leaves queued runs claimable by other nodes).
            // Waiting on the gate here also hands a finishing run's slot straight to the next queued run, with no
            // poll-interval gap in between.
            await gate.WaitAsync(ct).ConfigureAwait(false);

            var slotOwnedByRun = false;
            try
            {
                ClaimedRun? claim;
                await using (var scope = _services.CreateAsyncScope())
                {
                    var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                    claim = await RunQueueStore.ClaimNextAsync(catalog, _node, pools, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                }

                if (claim is not { } claimed)
                {
                    return; // queue drained
                }

                // Each claimed run executes on its own task with its own DI scope (a scope and its CatalogDbContext
                // are never shared across tasks). From here the task owns the slot and releases it when the run
                // reaches its end state; the continuation only prunes the in-flight map used by shutdown. It runs
                // under the abort token, never the claim loop's: a stop must not sever a run this node just started.
                var task = ExecuteClaimedAsync(claimed, gate, abortCt);
                slotOwnedByRun = true;
                inFlight[claimed.RunId] = task;
                _ = task.ContinueWith(
                    _ => inFlight.TryRemove(claimed.RunId, out Task? _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            finally
            {
                // A failed or cancelled claim (or an empty queue) never launched a run, so the slot goes back.
                if (!slotOwnedByRun)
                {
                    gate.Release();
                }
            }
        }
    }

    /// <summary>Executes one claimed run on its own DI scope and releases the concurrency slot when the run reaches
    /// its end state. Never throws: an abort (the drain window expired) leaves the run <c>running</c> for recovery,
    /// and every other failure has already been driven terminal (best-effort) by
    /// <see cref="RunClaimedAsync"/>, so one run can never kill the drain loop or a sibling run.</summary>
    /// <param name="claim">The claimed run's id and the claim's attempt (the fencing token for its outcome write).</param>
    /// <param name="gate">The node's run-concurrency gate; this run's slot is released when it ends.</param>
    /// <param name="abortCt">Trips only when a stopping node's drain window expires, never merely because the node
    /// was asked to stop; see <see cref="DrainInFlightAsync"/>.</param>
    private async Task ExecuteClaimedAsync(ClaimedRun claim, SemaphoreSlim gate, CancellationToken abortCt)
    {
        // A per-run source linked to the abort token: an operator cancel trips only this one (aborting just this
        // run), while an expired drain trips every run through the link. Registered before execution so a cancel
        // arriving the instant after the claim is still observed. Disposed only after the run ends, so a late cancel
        // never races a disposed source.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(abortCt);
        _running[claim.RunId] = runCts;
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await RunClaimedAsync(scope.ServiceProvider, catalog, claim, abortCt, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abortCt.IsCancellationRequested)
        {
            // The drain window expired and severed this run mid-flight: it stays 'running' so recovery requeues it.
        }
        catch (Exception ex)
        {
            // RunClaimedAsync drives run failures (and operator cancels) terminal itself; this guards the scope
            // plumbing around it.
            LogRunError(claim.RunId, SecretHygiene.RedactedMessage(ex));
        }
        finally
        {
            _running.TryRemove(claim.RunId, out _);
            gate.Release();
        }
    }

    /// <summary>Drains the compute-task queue exactly like <see cref="DrainAsync"/> drains runs: claim only while
    /// a slot is free (a saturated node leaves queued tasks claimable by other nodes), execute each claimed task on
    /// its own task with its own DI scope, and hand a finishing task's slot straight to the next one.</summary>
    private async Task DrainComputeAsync(
        IReadOnlyList<string> pools, SemaphoreSlim gate, ConcurrentDictionary<Guid, Task> inFlight, CancellationToken ct,
        CancellationToken abortCt)
    {
        while (!ct.IsCancellationRequested)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);

            var slotOwnedByTask = false;
            try
            {
                Guid? taskId;
                await using (var scope = _services.CreateAsyncScope())
                {
                    var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                    taskId = await ComputeTaskStore.ClaimNextAsync(catalog, _node, pools, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                }

                if (taskId is null)
                {
                    return; // queue drained
                }

                var task = ExecuteClaimedTaskAsync(taskId.Value, gate, abortCt);
                slotOwnedByTask = true;
                inFlight[taskId.Value] = task;
                _ = task.ContinueWith(
                    _ => inFlight.TryRemove(taskId.Value, out Task? _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            finally
            {
                if (!slotOwnedByTask)
                {
                    gate.Release();
                }
            }
        }
    }

    /// <summary>Executes one claimed compute task on its own DI scope and releases the slot when it reaches its
    /// end state. Never throws, mirroring <see cref="ExecuteClaimedAsync"/>: an abort (the drain window expired)
    /// leaves the task <c>running</c> for recovery; every other failure is driven terminal here.</summary>
    private async Task ExecuteClaimedTaskAsync(Guid taskId, SemaphoreSlim gate, CancellationToken abortCt)
    {
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(abortCt);
        _runningTasks[taskId] = taskCts;
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await RunClaimedTaskAsync(scope.ServiceProvider, catalog, taskId, abortCt, taskCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abortCt.IsCancellationRequested)
        {
            // The drain window expired and severed the task: it stays 'running' so recovery requeues it.
        }
        catch (Exception ex)
        {
            LogTaskError(taskId, SecretHygiene.RedactedMessage(ex));
        }
        finally
        {
            _runningTasks.TryRemove(taskId, out _);
            gate.Release();
        }
    }

    /// <param name="scope">The claimed task's own DI scope, never shared with another task.</param>
    /// <param name="catalog">The catalog context resolved from <paramref name="scope"/>.</param>
    /// <param name="taskId">The claimed compute task's id.</param>
    /// <param name="shutdownCt">The abort token: a trip (the drain window expired) leaves the task <c>running</c>
    /// for recovery.</param>
    /// <param name="taskCt">The per-task token (linked to shutdown): an operator cancel trips this alone, aborting
    /// the in-flight operation so the task records <c>cancelled</c> rather than requeued.</param>
    private async Task RunClaimedTaskAsync(
        IServiceProvider scope, CatalogDbContext catalog, Guid taskId, CancellationToken shutdownCt, CancellationToken taskCt)
    {
        var ct = shutdownCt;
        try
        {
            var row = await catalog.ComputeTasks.AsNoTracking()
                .Where(t => t.TaskId == taskId)
                .Select(t => new { t.Operation, t.SourceRef, t.ArgumentsJson })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null)
            {
                return; // removed between claim and load; nothing to run
            }

            LogTaskStarting(taskId, row.Operation, row.SourceRef);

            // The queue row is data from the database: parse AND re-validate it here, so a malformed or
            // hand-tampered payload fails the task with a precise message instead of reaching a target.
            ComputeTaskPayload payload;
            try
            {
                payload = ComputeTaskPayload.FromJson(row.ArgumentsJson);
            }
            catch (SqlFlowException ex)
            {
                await ComputeTaskStore.FailAsync(
                    catalog, taskId, SecretHygiene.RedactedMessage(ex), _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                return;
            }

            var executor = scope.GetRequiredService<ComputeTaskExecutor>();
            // The executor runs under the per-task token so an operator cancel aborts only the in-flight operation,
            // while the completion write below stays on the shutdown token (a late cancel never corrupts it).
            var resultJson = await executor.ExecuteAsync(payload, taskCt).ConfigureAwait(false);

            await ComputeTaskStore.CompleteAsync(catalog, taskId, resultJson, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            LogTaskSucceeded(taskId, row.Operation);
        }
        catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
        {
            // Shutdown: leave the task 'running' so the next start's recovery requeues it.
            throw;
        }
        catch (Exception) when (taskCt.IsCancellationRequested && !shutdownCt.IsCancellationRequested)
        {
            // The operator cancelled: the per-task token tripped and aborted the in-flight operation (surfaced as
            // OperationCanceledException or a transport exception). Record 'cancelled', not 'failed'.
            LogTaskCancelled(taskId);
            await TryCancelRunningTaskAsync(catalog, taskId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed task (unreachable target, bad argument, transport error) must never kill the worker: drive it
            // terminal (best-effort) and continue.
            LogTaskError(taskId, SecretHygiene.RedactedMessage(ex));
            await TryFailTaskAsync(catalog, taskId, SecretHygiene.RedactedMessage(ex)).ConfigureAwait(false);
        }
    }

    private async Task TryFailTaskAsync(CatalogDbContext catalog, Guid taskId, string error)
    {
        try
        {
            // The original token may be tripped (or the failure a database blip): a short independent deadline
            // still drives the task terminal where the catalog is reachable (mirrors TryFailAsync for runs).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ComputeTaskStore.FailAsync(catalog, taskId, error, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex));
        }
    }

    private async Task TryCancelRunningTaskAsync(CatalogDbContext catalog, Guid taskId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ComputeTaskStore.CancelRunningAsync(catalog, taskId, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <param name="scope">The claimed run's own DI scope, never shared with another run.</param>
    /// <param name="catalog">The catalog context resolved from <paramref name="scope"/>.</param>
    /// <param name="claim">The claimed run's id (the orchestrator-assigned id the trigger returned) and the claim's
    /// attempt: the fencing token every outcome write below presents, so if crash recovery requeues this run out
    /// from under a node presumed dead, that node's late writes are dropped instead of clobbering the successor
    /// execution's outcome.</param>
    /// <param name="shutdownCt">The abort token: it trips only when a stopping node's drain window expired, and the
    /// run is then left <c>running</c> for recovery (never recorded terminal), so severed work is never lost.</param>
    /// <param name="runCt">The per-run token (linked to shutdown): an operator cancel trips this alone, aborting the
    /// flow's in-flight work so the run is recorded <c>cancelled</c> rather than requeued.</param>
    private async Task RunClaimedAsync(
        IServiceProvider scope, CatalogDbContext catalog, ClaimedRun claim, CancellationToken shutdownCt, CancellationToken runCt)
    {
        var (runId, attempt) = claim;
        var ct = shutdownCt;
        try
        {
            // One joined projection instead of three or four sequential lookups: the run row, its repo and
            // pipeline, and the repo's managed source (used only by SHA-pinned runs) arrive in a single round
            // trip. Left joins keep the missing cases distinguishable, so every failure message stays precise.
            var run = await (
                    from r in catalog.Runs.AsNoTracking()
                    where r.RunId == runId
                    join repoRow in catalog.Repos.AsNoTracking() on r.RepoId equals (Guid?)repoRow.Id into repoRows
                    from repo in repoRows.DefaultIfEmpty()
                    join pipelineRow in catalog.Pipelines.AsNoTracking() on r.PipelineId equals pipelineRow.Id into pipelineRows
                    from pipeline in pipelineRows.DefaultIfEmpty()
                    join sourceRow in catalog.RepoSources.AsNoTracking() on repo.Name equals sourceRow.Name into sourceRows
                    from source in sourceRows.DefaultIfEmpty()
                    select new
                    {
                        r.RepoId,
                        r.FlowName,
                        r.CommitSha,
                        r.FlowVersionHash,
                        r.ParametersJson,
                        r.TriggerSource,
                        r.RequestedBy,
                        RepoName = repo != null ? repo.Name : null,
                        RepoRemoteUrl = repo != null ? repo.RemoteUrl : null,
                        RepoRootPath = repo != null ? repo.RootPath : null,
                        PipelineRelativePath = pipeline != null ? pipeline.RelativePath : null,
                        CredentialReference = source != null ? source.CredentialReference : null,
                        CredentialUsername = source != null ? source.CredentialUsername : null,
                    })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (run is null)
            {
                return; // cancelled/removed between claim and load; nothing to run
            }

            if (run.RepoId is not { } repoId)
            {
                await FailAsync(catalog, runId, attempt, "the run is not attributed to a repository.", ct).ConfigureAwait(false);
                return;
            }

            // Repo.Name and Pipeline.RelativePath are required columns on their tables, so a null projection can
            // only mean the left join found no row: the repo or pipeline has since left the catalog.
            if (run.RepoName is not { } repoName || run.PipelineRelativePath is not { } relativePath)
            {
                await FailAsync(catalog, runId, attempt, "the run's repository or pipeline is no longer in the catalog.", ct).ConfigureAwait(false);
                return;
            }

            string flowRoot;

            // Snapshot-first: the enqueue stamped the run with the content hash of the exact YAML to execute and
            // staged that version in the catalog, so the node writes it into its local version cache and runs it
            // with no git access at all. This is what keeps a schedule fanning out a whole batch from storming the
            // git remote with clones (the failure mode: the remote answers a burst of authenticated fetches with
            // throttling, which libgit2 surfaces as "too many redirects or authentication replays"). The git
            // materialization below remains the fallback for a run that carries no snapshot (enqueued before the
            // snapshot model, or its flow embeds a literal credential), a pruned version row, and a document that
            // needs the surrounding repo tree (a relative local path).
            var snapshotRoot = !string.IsNullOrWhiteSpace(run.FlowVersionHash)
                ? await TryStageSnapshotAsync(scope, catalog, runId, run.FlowVersionHash, relativePath, ct).ConfigureAwait(false)
                : null;
            if (snapshotRoot is not null)
            {
                flowRoot = snapshotRoot;
            }
            else if (!string.IsNullOrWhiteSpace(run.CommitSha))
            {
                // SHA-pinned: run the exact committed version, materialized from the repo's remote (reproducible,
                // and works even on a node with no locally synced copy of this flow).
                if (string.IsNullOrWhiteSpace(run.RepoRemoteUrl))
                {
                    await FailAsync(catalog, runId, attempt,
                        $"run is pinned to commit '{run.CommitSha}' but repository '{repoName}' has no remote URL to materialize from.", ct).ConfigureAwait(false);
                    return;
                }

                // The git credential comes from the repo's registered source (matched by name, projected above),
                // whose stored ${...} reference points at the vault/env holding the token; a repo synced by the
                // CLI with no source falls back to the host environment. The node fetches the secret itself: it is
                // the mobile execution engine that resolves what it needs, and only the reference travelled
                // through the catalog.
                var resolver = scope.GetRequiredService<ISecretResolver>();
                var credentials = await GitMaterializer
                    .ResolveCredentialsAsync(resolver, run.CredentialReference, run.CredentialUsername, ct)
                    .ConfigureAwait(false);
                flowRoot = _materializer.Materialize(run.RepoRemoteUrl, run.CommitSha, credentials, ct);
                LogMaterialized(runId, repoName, run.CommitSha);
            }
            else
            {
                // Unpinned: run from the node's locally synced repo path (the default).
                if (string.IsNullOrWhiteSpace(run.RepoRootPath))
                {
                    await FailAsync(catalog, runId, attempt, $"repository '{repoName}' has no synced root path on this node.", ct).ConfigureAwait(false);
                    return;
                }

                flowRoot = run.RepoRootPath;
            }

            var flowFile = Path.GetFullPath(Path.Combine(flowRoot, relativePath));
            if (!File.Exists(flowFile))
            {
                await FailAsync(catalog, runId, attempt, $"the flow file for '{run.FlowName}' was not found on this node.", ct).ConfigureAwait(false);
                return;
            }

            LogStarting(runId, run.FlowName, repoName);

            var documents = scope.GetRequiredService<YamlDocumentLoader>();
            var document = DocumentLoader.Load(documents, flowFile, message => LogHygiene(runId, message));

            // The claimed run id is the orchestrator-assigned id: the engine stamps it on the run and its artifact,
            // so the run records under exactly the id the trigger returned. Run-log echo stays null (server side).
            // The run's parameters travel from the queue row into the engine here: the one handoff point. The row
            // is data from the database, so the stored JSON is parsed and re-validated before the engine sees it.
            var parameters = RunParameters.FromJson(run.ParametersJson);
            parameters.Validate();
            if (!parameters.IsDefault)
            {
                LogParameters(runId, parameters.Describe());
            }

            // The node streams the run's canonical events into the catalog live: as each event is published a
            // CatalogRunEvent row is written on the sink's own scope/context, so the run's trace updates while the
            // run is still running and survives even a mid-run crash. The sink is disposed at the end of this block
            // (draining every queued write) BEFORE the completion write-back below, which appends only the tail the
            // live feed did not write: the artifact stays authoritative.
            DocumentExecutionResult exec;
            await using (var eventSink = new CatalogRunEventSink(_services, runId, repoId, _logger))
            {
                var options = new DocumentExecutionOptions
                {
                    RunId = runId, Echo = null, Parameters = parameters, EventSink = eventSink,
                    // The claimed run's flow name selects WHICH flow of the document executes.
                    FlowName = run.FlowName,
                    // Who asked (the person or client that triggered it, else the schedule or source that did), for
                    // the kinds that keep an audit trail.
                    Actor = run.RequestedBy ?? run.TriggerSource,
                };
                // The executor runs under the per-run token: an operator cancel aborts the in-flight work here (and
                // only here), while the surrounding bookkeeping stays on the shutdown token so a late cancel never
                // corrupts the completion write.
                exec = await _executor.ExecuteAsync(document, flowFile, options, runCt).ConfigureAwait(false);
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            if (exec.RunDirectory is { } runDirectory)
            {
                var runJson = Path.Combine(runDirectory, "run.json");
                var outcome = await RunQueueStore.CompleteFromArtifactAsync(
                    catalog, runId, repoId, runJson, now, _node, attempt, ct).ConfigureAwait(false);
                if (outcome == RunCompletionOutcome.StaleClaim)
                {
                    // This node was presumed dead and the run was requeued (and possibly re-claimed) out from under
                    // it: the fence dropped this write, the successor execution's outcome is authoritative, and the
                    // flow's idempotent delivery makes the double execution harmless. Loud in the log because it
                    // means this node's heartbeats went unseen for the reaper's whole stale window.
                    LogStaleClaim(runId, attempt);
                }
            }
            else
            {
                // No artifact was written (an IO failure while writing the run history): record the outcome directly
                // so the run still reaches a terminal state.
                await FailAsync(catalog, runId, attempt, SecretHygiene.RedactedMessage(exec.Error ?? "the run produced no artifact."), ct).ConfigureAwait(false);
            }

            if (exec.Success)
            {
                LogSucceeded(runId, run.FlowName, exec.DurationSeconds);
            }
            else
            {
                LogFailed(runId, run.FlowName, SecretHygiene.RedactedMessage(exec.Error ?? "(no error message)"));
            }
        }
        catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
        {
            // Shutdown cancelled this run mid-flight: leave it 'running' so the next start's recovery requeues it.
            throw;
        }
        catch (Exception) when (runCt.IsCancellationRequested && !shutdownCt.IsCancellationRequested)
        {
            // The operator cancelled this run: the per-run token tripped and aborted the in-flight work. Record it
            // 'cancelled' - not 'failed' - and continue to the next claim.
            LogCancelled(runId);
            await TryCancelRunningAsync(catalog, runId, attempt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed run (bad flow, unreachable target, IO) must never kill the worker: drive it to a terminal
            // state (best-effort) and continue to the next claim.
            LogRunError(runId, SecretHygiene.RedactedMessage(ex));
            await TryFailAsync(catalog, runId, attempt, SecretHygiene.RedactedMessage(ex)).ConfigureAwait(false);
        }
    }

    // The staged-YAML version cache: one immutable directory per content hash, under the same per-user node cache
    // the git materializer writes its checkouts to. The "yaml" namespace can never collide with a repo directory
    // (those are keyed by a 16-char remote-URL hash). Stability matters: runs of the same version share the
    // directory, so the run history written next to the flow file accumulates across runs exactly as it does in a
    // per-commit git checkout.
    private static readonly string SnapshotCacheRoot =
        Path.Combine(Path.GetTempPath(), "sqlflow", "node-cache", "yaml");

    /// <summary>
    /// Stages the run's snapshotted YAML version from the catalog into the local version cache and returns the
    /// directory to execute from (the flow file lands at its repo-relative path beneath it, so the run-history
    /// anchor matches a git checkout's layout). Returns null, sending the caller to the git materialization path,
    /// when the version row no longer exists, the document cannot execute from a bare snapshot (it references
    /// sibling repo files through a relative local path), the stored text does not parse (the git path reproduces
    /// the same load error the run would have reported before snapshots), or the cache directory cannot be
    /// written (logged; the git path is the still-correct degradation).
    /// </summary>
    private async Task<string?> TryStageSnapshotAsync(
        IServiceProvider scope, CatalogDbContext catalog, Guid runId, string contentHash, string relativePath,
        CancellationToken ct)
    {
        var yaml = await catalog.FlowVersions.AsNoTracking()
            .Where(v => v.ContentHash == contentHash)
            .Select(v => v.Yaml)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null; // the version row is gone; the commit pin still identifies the exact content in git
        }

        var versionRoot = Path.Combine(SnapshotCacheRoot, contentHash);
        var flowFile = Path.GetFullPath(Path.Combine(versionRoot, relativePath));

        // One probing parse decides executability from a bare snapshot; the run itself still loads through the
        // same DocumentLoader.Load path as every other execution mode (CLI file, git checkout).
        try
        {
            var documents = scope.GetRequiredService<YamlDocumentLoader>();
            if (documents.Parse(yaml, flowFile).RequiresRepoTree)
            {
                return null;
            }
        }
        catch (SqlFlowException)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(flowFile)!);
            if (!File.Exists(flowFile))
            {
                // Content-addressed, so concurrent runs of the same version race benignly (identical bytes):
                // publish with a private temp write + move, so a torn write is never observed and a move that
                // loses the race just means a sibling run staged it first.
                var temp = flowFile + ".staging-" + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(temp, yaml, ct).ConfigureAwait(false);
                    File.Move(temp, flowFile);
                }
                catch (IOException) when (File.Exists(flowFile))
                {
                    // A concurrent run published this version between the existence check and the move.
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }

            LogSnapshotStaged(runId, contentHash);
            return versionRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The local cache is unwritable (disk pressure, permissions): degrade to the git path, which needs no
            // pre-staged file, and say why so the slower clone is explainable from the log.
            LogSnapshotStageFailed(runId, contentHash, SecretHygiene.RedactedMessage(ex));
            return null;
        }
    }

    private Task FailAsync(CatalogDbContext catalog, Guid runId, int attempt, string error, CancellationToken ct)
        => RunQueueStore.FailAsync(catalog, runId, error, _clock.GetUtcNow().UtcDateTime, _node, attempt, ct);

    private async Task TryFailAsync(CatalogDbContext catalog, Guid runId, int attempt, string error)
    {
        try
        {
            // The original cancellation token may be tripped (or the failure may have been a database blip): use a
            // short independent deadline so the run is still driven terminal where the catalog is reachable.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunQueueStore.FailAsync(catalog, runId, error, _clock.GetUtcNow().UtcDateTime, _node, attempt, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex));
        }
    }

    private async Task TryCancelRunningAsync(CatalogDbContext catalog, Guid runId, int attempt)
    {
        try
        {
            // The per-run token that triggered this is already tripped, so record the outcome on a short independent
            // deadline (mirroring TryFailAsync) - the run must still reach 'cancelled' rather than linger 'running'.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunQueueStore.CancelRunningAsync(catalog, runId, _clock.GetUtcNow().UtcDateTime, _node, attempt, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: materialized repo '{Repo}' at commit {CommitSha}.")]
    private partial void LogMaterialized(Guid runId, string repo, string commitSha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: staged flow version {ContentHash} from the catalog snapshot (no git access needed).")]
    private partial void LogSnapshotStaged(Guid runId, string contentHash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: could not stage flow version {ContentHash} into the local cache ({Error}); falling back to git materialization.")]
    private partial void LogSnapshotStageFailed(Guid runId, string contentHash, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: run parameters applied: {Parameters}.")]
    private partial void LogParameters(Guid runId, string parameters);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} starting: flow '{FlowName}' in repo '{Repo}'.")]
    private partial void LogStarting(Guid runId, string flowName, string repo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} succeeded: flow '{FlowName}' in {DurationSeconds}s.")]
    private partial void LogSucceeded(Guid runId, string flowName, double durationSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} failed: flow '{FlowName}': {Error}")]
    private partial void LogFailed(Guid runId, string flowName, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: operator cancel observed; aborting the in-flight work.")]
    private partial void LogCancelling(Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} cancelled by operator; recorded cancelled.")]
    private partial void LogCancelled(Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} threw and was driven to failed: {Error}")]
    private partial void LogRunError(Guid runId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run queue poll error: {Error}")]
    private partial void LogPollError(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {Count} run(s) left running by a previous worker incarnation; requeued.")]
    private partial void LogRecovered(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping: no longer claiming; draining {Count} in-flight item(s) so each records its own outcome (up to {DrainSeconds}s).")]
    private partial void LogDraining(int count, int drainSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drained: every in-flight item finished and recorded its outcome.")]
    private partial void LogDrained();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Drain window of {DrainSeconds}s expired with work still in flight; cancelling it. Each severed run stays 'running' and is requeued by the reaper, which consumes one of its execution attempts. Raise the drain window, or the platform's termination grace period, if this recurs.")]
    private partial void LogDrainTimedOut(int drainSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: outcome write dropped by the claim fence (attempt {Attempt}): the run was requeued out from under this node while it executed, so the successor execution's outcome is authoritative. This node's heartbeats went unseen for the reaper's whole stale window; check for catalog connectivity gaps or a paused container.")]
    private partial void LogStaleClaim(Guid runId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart requested at {RequestedUtc:o}; draining in-flight work and exiting so the orchestrator recreates this node.")]
    private partial void LogRestartRequested(DateTime requestedUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {Count} compute task(s) left running by a previous worker incarnation; requeued.")]
    private partial void LogRecoveredTasks(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId} starting: {Operation} against {SourceRef}.")]
    private partial void LogTaskStarting(Guid taskId, string operation, string sourceRef);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId} succeeded ({Operation}).")]
    private partial void LogTaskSucceeded(Guid taskId, string operation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId}: operator cancel observed; aborting the in-flight operation.")]
    private partial void LogTaskCancelling(Guid taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compute task {TaskId} cancelled by operator; recorded cancelled.")]
    private partial void LogTaskCancelled(Guid taskId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Compute task {TaskId} threw and was driven to failed: {Error}")]
    private partial void LogTaskError(Guid taskId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: {Message}")]
    private partial void LogHygiene(Guid runId, string message);
}
