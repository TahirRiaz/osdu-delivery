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
/// The compute-node runtime: it drains the durable run queue by atomically claiming the oldest queued run (so two
/// nodes never run the same flow), resolving the flow file from the catalog (repo root + relative path), executing
/// it through the shared <see cref="DocumentExecutor"/> (identical to a CLI run), and recording the outcome from
/// the produced artifact under the run id the trigger already returned. The queue is the database, so the runtime
/// is stateless and horizontally scalable: the control plane hosts it in-process, and a self-hosted
/// <c>sqlflow worker</c> hosts the very same loop on a node inside a private network. Each node resolves every
/// credential from its own environment, so nothing sensitive travels through the queue.
/// </summary>
/// <remarks>
/// Robustness: one run's failure never tears down the loop (per-run try/catch drives the run to a terminal state and
/// continues); a poll/claim error (a transient database outage) is logged and retried next tick. On startup it
/// requeues any run this node left <c>running</c> (an orphan from a previous incarnation that stopped mid-run). On
/// shutdown it stops claiming and lets the in-flight run finish, or, if cancelled, leaves it <c>running</c> for the
/// next start to recover. All diagnostics are logged with secret-redacted messages.
/// </remarks>
public sealed partial class RunWorker
{
    private readonly IServiceProvider _services;
    private readonly DocumentExecutor _executor;
    private readonly TimeProvider _clock;
    private readonly ILogger<RunWorker> _logger;
    private readonly string _node = Environment.MachineName;
    private readonly string? _version = typeof(RunWorker).Assembly.GetName().Version?.ToString();
    private readonly GitMaterializer _materializer = new();

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
    /// </summary>
    public async Task RunAsync(
        TimeSpan pollInterval, IReadOnlyList<string> pools, Func<TimeSpan, CancellationToken, Task> waitForWork, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(waitForWork);

        await RecoverOrphansAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await HeartbeatAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await DrainAsync(pools, stoppingToken).ConfigureAwait(false);
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

    private async Task DrainAsync(IReadOnlyList<string> pools, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

            var runId = await RunQueueStore.ClaimNextAsync(catalog, _node, pools, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (runId is null)
            {
                return; // queue drained
            }

            await RunClaimedAsync(scope.ServiceProvider, catalog, runId.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task RunClaimedAsync(IServiceProvider scope, CatalogDbContext catalog, Guid runId, CancellationToken ct)
    {
        try
        {
            var run = await catalog.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
            if (run is null)
            {
                return; // cancelled/removed between claim and load; nothing to run
            }

            if (run.RepoId is not { } repoId)
            {
                await FailAsync(catalog, runId, "the run is not attributed to a repository.", ct).ConfigureAwait(false);
                return;
            }

            var repo = await catalog.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repoId, ct).ConfigureAwait(false);
            var pipeline = await catalog.Pipelines.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == run.PipelineId, ct).ConfigureAwait(false);
            if (repo is null || pipeline is null)
            {
                await FailAsync(catalog, runId, "the run's repository or pipeline is no longer in the catalog.", ct).ConfigureAwait(false);
                return;
            }

            string flowRoot;
            if (!string.IsNullOrWhiteSpace(run.CommitSha))
            {
                // SHA-pinned: run the exact committed version, materialized from the repo's remote (reproducible,
                // and works even on a node with no locally synced copy of this flow).
                if (string.IsNullOrWhiteSpace(repo.RemoteUrl))
                {
                    await FailAsync(catalog, runId,
                        $"run is pinned to commit '{run.CommitSha}' but repository '{repo.Name}' has no remote URL to materialize from.", ct).ConfigureAwait(false);
                    return;
                }

                flowRoot = _materializer.Materialize(repo.RemoteUrl, run.CommitSha, GitMaterializer.CredentialsFromEnvironment(), ct);
                LogMaterialized(runId, repo.Name, run.CommitSha);
            }
            else
            {
                // Unpinned: run from the node's locally synced repo path (the default).
                if (string.IsNullOrWhiteSpace(repo.RootPath))
                {
                    await FailAsync(catalog, runId, $"repository '{repo.Name}' has no synced root path on this node.", ct).ConfigureAwait(false);
                    return;
                }

                flowRoot = repo.RootPath;
            }

            var flowFile = Path.GetFullPath(Path.Combine(flowRoot, pipeline.RelativePath));
            if (!File.Exists(flowFile))
            {
                await FailAsync(catalog, runId, $"the flow file for '{run.FlowName}' was not found on this node.", ct).ConfigureAwait(false);
                return;
            }

            LogStarting(runId, run.FlowName, repo.Name);

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
            var exec = await _executor.ExecuteAsync(document, flowFile, options, ct).ConfigureAwait(false);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown cancelled this run mid-flight: leave it 'running' so the next start's recovery requeues it.
            throw;
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Run {RunId} threw and was driven to failed: {Error}")]
    private partial void LogRunError(Guid runId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run queue poll error: {Error}")]
    private partial void LogPollError(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {Count} run(s) left running by a previous worker incarnation; requeued.")]
    private partial void LogRecovered(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: {Message}")]
    private partial void LogHygiene(Guid runId, string message);
}
