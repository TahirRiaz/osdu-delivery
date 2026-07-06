using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Node;

/// <summary>
/// The compute-node runtime: it drains the durable run queue by atomically claiming the oldest queued runs (so two
/// nodes never run the same flow) and executing up to a bounded number of them concurrently, each on its own DI
/// scope, resolving the flow file from the catalog (repo root + relative path), executing it through the shared
/// <see cref="DocumentExecutor"/> (identical to a CLI run), and recording the outcome from the produced artifact
/// under the run id the trigger already returned. The queue is the database, so the runtime is stateless and
/// horizontally scalable: the control plane hosts it in-process, and a self-hosted <c>sqlflow worker</c> hosts the
/// very same loop on a node inside a private network. Each node resolves every credential from its own
/// environment, so nothing sensitive travels through the queue.
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

    private readonly IServiceProvider _services;
    private readonly DocumentExecutor _executor;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunWorker> _logger;
    private readonly string _node = Environment.MachineName;
    private readonly string? _version = typeof(RunWorker).Assembly.GetName().Version?.ToString();
    private readonly GitMaterializer _materializer = new();

    // The runs this node is currently executing, keyed by run id, each with its own cancellation source linked to
    // the shutdown token. An operator cancel of a running run trips its source (see PollCancellationsAsync), which
    // aborts the in-flight statement; ExecuteClaimedAsync registers a run here before it starts and removes it when
    // it ends. Concurrent because the drain loop registers while executing tasks remove.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public RunWorker(IServiceProvider services, DocumentExecutor executor, TimeProvider clock, ILogger<RunWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _executor = executor;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>This node's identity (its machine name), stamped on a claimed run so its work is attributable and
    /// recoverable.</summary>
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
    /// </summary>
    public async Task RunAsync(
        TimeSpan pollInterval, IReadOnlyList<string> pools, Func<TimeSpan, CancellationToken, Task> waitForWork,
        CancellationToken stoppingToken, int maxConcurrentRuns = DefaultMaxConcurrentRuns)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(waitForWork);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentRuns, 1);

        await RecoverOrphansAsync(stoppingToken).ConfigureAwait(false);

        using var gate = new SemaphoreSlim(maxConcurrentRuns, maxConcurrentRuns);
        var inFlight = new ConcurrentDictionary<Guid, Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            await HeartbeatAsync(stoppingToken).ConfigureAwait(false);
            await PollCancellationsAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await DrainAsync(pools, gate, inFlight, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A poll/claim error (a transient database outage) must not end the worker; log and try next tick.
                LogPollError(SecretHygiene.RedactedMessage(ex.Message));
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

        // Shutdown: claiming has stopped; wait for the in-flight runs. Each either finishes cleanly (recording
        // its outcome) or observes the cancellation and leaves its row 'running' for the next start's recovery.
        // The run tasks never fault (ExecuteClaimedAsync catches everything), so this wait cannot throw, and it
        // keeps the gate alive until every slot is released.
        var pending = inFlight.Values.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await NodeStore.HeartbeatAsync(catalog, _node, _version, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; nothing to do.
        }
        catch (Exception ex)
        {
            // The fleet heartbeat is best-effort: a failure must never affect draining; the next poll retries it.
            LogPollError(SecretHygiene.RedactedMessage(ex.Message));
        }
    }

    /// <summary>Observes operator cancel requests for the runs this node is executing and trips each matching run's
    /// cancellation token, which aborts its in-flight statement (SqlClient sends an attention to the server) and
    /// drives it to <c>cancelled</c>. Best-effort like the heartbeat: it only queries when this node has in-flight
    /// runs, and a transient catalog error is logged and retried on the next poll rather than stopping the loop.</summary>
    private async Task PollCancellationsAsync(CancellationToken ct)
    {
        if (_running.IsEmpty)
        {
            return;
        }

        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; nothing to do.
        }
        catch (Exception ex)
        {
            // Observing cancels is best-effort: a failure must never affect draining; the next poll retries it.
            LogPollError(SecretHygiene.RedactedMessage(ex.Message));
        }
    }

    private async Task RecoverOrphansAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var recovered = await RunQueueStore.RecoverStuckRunningAsync(catalog, _node, ct).ConfigureAwait(false);
            if (recovered > 0)
            {
                LogRecovered(recovered);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down during startup recovery; nothing to do.
        }
        catch (Exception ex)
        {
            // Recovery is best-effort: if the catalog is briefly unreachable at startup, the next poll retries the
            // claim path anyway. Do not let it stop the worker from starting.
            LogPollError(SecretHygiene.RedactedMessage(ex.Message));
        }
    }

    private async Task DrainAsync(
        IReadOnlyList<string> pools, SemaphoreSlim gate, ConcurrentDictionary<Guid, Task> inFlight, CancellationToken ct)
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
                Guid? runId;
                await using (var scope = _services.CreateAsyncScope())
                {
                    var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                    runId = await RunQueueStore.ClaimNextAsync(catalog, _node, pools, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                }

                if (runId is null)
                {
                    return; // queue drained
                }

                // Each claimed run executes on its own task with its own DI scope (a scope and its CatalogDbContext
                // are never shared across tasks). From here the task owns the slot and releases it when the run
                // reaches its end state; the continuation only prunes the in-flight map used by shutdown.
                var task = ExecuteClaimedAsync(runId.Value, gate, ct);
                slotOwnedByRun = true;
                inFlight[runId.Value] = task;
                _ = task.ContinueWith(
                    _ => inFlight.TryRemove(runId.Value, out Task? _),
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
    /// its end state. Never throws: a shutdown cancellation leaves the run <c>running</c> for the next start's
    /// recovery, and every other failure has already been driven terminal (best-effort) by
    /// <see cref="RunClaimedAsync"/>, so one run can never kill the drain loop or a sibling run.</summary>
    private async Task ExecuteClaimedAsync(Guid runId, SemaphoreSlim gate, CancellationToken stoppingToken)
    {
        // A per-run source linked to the shutdown token: an operator cancel trips only this one (aborting just this
        // run), while shutdown trips every run through the link. Registered before execution so a cancel arriving
        // the instant after the claim is still observed. Disposed only after the run ends, so a late cancel never
        // races a disposed source.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[runId] = runCts;
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await RunClaimedAsync(scope.ServiceProvider, catalog, runId, stoppingToken, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown cancelled this run mid-flight: it stays 'running' so the next start's recovery requeues it.
        }
        catch (Exception ex)
        {
            // RunClaimedAsync drives run failures (and operator cancels) terminal itself; this guards the scope
            // plumbing around it.
            LogRunError(runId, SecretHygiene.RedactedMessage(ex.Message));
        }
        finally
        {
            _running.TryRemove(runId, out _);
            gate.Release();
        }
    }

    /// <param name="shutdownCt">The node's shutdown token: when it trips, the run is left <c>running</c> for the next
    /// start's recovery (never recorded terminal), so a stop-then-start never loses in-flight work.</param>
    /// <param name="runCt">The per-run token (linked to shutdown): an operator cancel trips this alone, aborting the
    /// flow's in-flight statement so the run is recorded <c>cancelled</c> rather than requeued.</param>
    private async Task RunClaimedAsync(
        IServiceProvider scope, CatalogDbContext catalog, Guid runId, CancellationToken shutdownCt, CancellationToken runCt)
    {
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
                        r.FullLoad,
                        r.BackfillFrom,
                        r.BackfillTo,
                        r.FilePattern,
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
                await FailAsync(catalog, runId, "the run is not attributed to a repository.", ct).ConfigureAwait(false);
                return;
            }

            // Repo.Name and Pipeline.RelativePath are required columns on their tables, so a null projection can
            // only mean the left join found no row: the repo or pipeline has since left the catalog.
            if (run.RepoName is not { } repoName || run.PipelineRelativePath is not { } relativePath)
            {
                await FailAsync(catalog, runId, "the run's repository or pipeline is no longer in the catalog.", ct).ConfigureAwait(false);
                return;
            }

            string flowRoot;
            if (!string.IsNullOrWhiteSpace(run.CommitSha))
            {
                // SHA-pinned: run the exact committed version, materialized from the repo's remote (reproducible,
                // and works even on a node with no locally synced copy of this flow).
                if (string.IsNullOrWhiteSpace(run.RepoRemoteUrl))
                {
                    await FailAsync(catalog, runId,
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
                    await FailAsync(catalog, runId, $"repository '{repoName}' has no synced root path on this node.", ct).ConfigureAwait(false);
                    return;
                }

                flowRoot = run.RepoRootPath;
            }

            var flowFile = Path.GetFullPath(Path.Combine(flowRoot, relativePath));
            if (!File.Exists(flowFile))
            {
                await FailAsync(catalog, runId, $"the flow file for '{run.FlowName}' was not found on this node.", ct).ConfigureAwait(false);
                return;
            }

            LogStarting(runId, run.FlowName, repoName);

            var documents = scope.GetRequiredService<YamlDocumentLoader>();
            var document = DocumentLoader.Load(documents, flowFile, message => LogHygiene(runId, message));

            // The claimed run id is the orchestrator-assigned id: the engine stamps it on the run and its artifact,
            // so the run records under exactly the id the trigger returned. Run-log echo stays null (server side).
            // The run's substitution parameters (the built-in backfill) travel from the queue row into the engine
            // here: the one handoff point, shared by every flow kind.
            var parameters = new RunParameters
            {
                FullLoad = run.FullLoad,
                BackfillFrom = run.BackfillFrom,
                BackfillTo = run.BackfillTo,
                FilePattern = run.FilePattern,
            };
            if (!parameters.IsDefault)
            {
                LogParameters(runId, parameters.Describe());
            }

            var options = new DocumentExecutionOptions { RunId = runId, Echo = null, Parameters = parameters };
            // The executor runs under the per-run token: an operator cancel aborts the in-flight statement here (and
            // only here), while the surrounding bookkeeping stays on the shutdown token so a late cancel never
            // corrupts the completion write.
            var exec = await _executor.ExecuteAsync(document, flowFile, options, runCt).ConfigureAwait(false);

            var now = _clock.GetUtcNow().UtcDateTime;
            if (exec.RunDirectory is { } runDirectory)
            {
                var runJson = Path.Combine(runDirectory, "run.json");
                await RunQueueStore.CompleteFromArtifactAsync(catalog, runId, repoId, runJson, now, ct).ConfigureAwait(false);
            }
            else
            {
                // No artifact was written (an IO failure while writing the run history): record the outcome directly
                // so the run still reaches a terminal state.
                await FailAsync(catalog, runId, SecretHygiene.RedactedMessage(exec.Error ?? "the run produced no artifact."), ct).ConfigureAwait(false);
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
            // The operator cancelled this run: the per-run token tripped and aborted the in-flight statement (which
            // SqlClient may surface as OperationCanceledException or a SqlException), so its transaction rolled back.
            // Record it 'cancelled' - not 'failed' - and continue to the next claim.
            LogCancelled(runId);
            await TryCancelRunningAsync(catalog, runId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed run (bad flow, unreachable database, IO) must never kill the worker: drive it to a terminal
            // state (best-effort) and continue to the next claim.
            LogRunError(runId, SecretHygiene.RedactedMessage(ex.Message));
            await TryFailAsync(catalog, runId, SecretHygiene.RedactedMessage(ex.Message)).ConfigureAwait(false);
        }
    }

    private Task FailAsync(CatalogDbContext catalog, Guid runId, string error, CancellationToken ct)
        => RunQueueStore.FailAsync(catalog, runId, error, _clock.GetUtcNow().UtcDateTime, ct);

    private async Task TryFailAsync(CatalogDbContext catalog, Guid runId, string error)
    {
        try
        {
            // The original cancellation token may be tripped (or the failure may have been a database blip): use a
            // short independent deadline so the run is still driven terminal where the catalog is reachable.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunQueueStore.FailAsync(catalog, runId, error, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex.Message));
        }
    }

    private async Task TryCancelRunningAsync(CatalogDbContext catalog, Guid runId)
    {
        try
        {
            // The per-run token that triggered this is already tripped, so record the outcome on a short independent
            // deadline (mirroring TryFailAsync) - the run must still reach 'cancelled' rather than linger 'running'.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunQueueStore.CancelRunningAsync(catalog, runId, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPollError(SecretHygiene.RedactedMessage(ex.Message));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: materialized repo '{Repo}' at commit {CommitSha}.")]
    private partial void LogMaterialized(Guid runId, string repo, string commitSha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: substitution parameters applied: {Parameters}.")]
    private partial void LogParameters(Guid runId, string parameters);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} starting: flow '{FlowName}' in repo '{Repo}'.")]
    private partial void LogStarting(Guid runId, string flowName, string repo);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} succeeded: flow '{FlowName}' in {DurationSeconds}s.")]
    private partial void LogSucceeded(Guid runId, string flowName, double durationSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} failed: flow '{FlowName}': {Error}")]
    private partial void LogFailed(Guid runId, string flowName, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId}: operator cancel observed; aborting the in-flight statement.")]
    private partial void LogCancelling(Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Run {RunId} cancelled by operator; recorded cancelled.")]
    private partial void LogCancelled(Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} threw and was driven to failed: {Error}")]
    private partial void LogRunError(Guid runId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run queue poll error: {Error}")]
    private partial void LogPollError(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {Count} run(s) left running by a previous worker incarnation; requeued.")]
    private partial void LogRecovered(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: {Message}")]
    private partial void LogHygiene(Guid runId, string message);
}
