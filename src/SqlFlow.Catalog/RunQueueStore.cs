using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Catalog;

/// <summary>What to enqueue: references only (never a secret). The flow is addressed by repo + name; an optional
/// pool routes it to eligible nodes, and an optional commit SHA pins it to an exact git version the node
/// materializes. A null <see cref="CommitSha"/> is not "unpinned" but "default": enqueueing pins the run to the
/// repo's last synced commit when one is known (see <see cref="RunQueueStore.EnqueueAsync"/>), so any node in the
/// fleet can execute it. Bundled into one request so the two optional references can never be passed in the wrong
/// order.</summary>
public sealed record RunEnqueueRequest(
    Guid RepoId, string FlowName, string FlowKind, string? TargetPool = null, string? CommitSha = null);

/// <summary>The result of a cancel request, so the API can answer 200 / 404 / 409 precisely.</summary>
public enum CancelOutcome
{
    /// <summary>The run was queued and is now cancelled.</summary>
    Cancelled,

    /// <summary>No run with that id exists.</summary>
    NotFound,

    /// <summary>The run exists but is no longer queued (already running or finished), so it cannot be cancelled.</summary>
    NotCancellable,
}

/// <summary>
/// The durable run queue: the <see cref="CatalogRun"/> table IS the queue, so queued and running work survives a
/// host restart, is visible to the read API, and (with the atomic claim below) can be drained by more than one
/// worker without ever double-running a flow. A control-plane trigger or a schedule enqueues a run; a worker claims
/// the oldest queued run, executes it through the shared engine, then records the outcome from its on-disk
/// artifact. This is distinct from <see cref="CatalogSync"/>, which maps already-finished on-disk runs into the
/// catalog; both share the run.json projection and the retriable-transaction helper, so there is one mapping path.
/// Stateless (pure operations over the supplied context and clock), like <see cref="CatalogProjection"/>.
/// </summary>
public static class RunQueueStore
{
    // The reliable single-statement claim: atomically pick the oldest queued run and flip it to running, returning
    // its id. UPDLOCK takes the update lock up front (no lock-upgrade race), READPAST skips rows another worker has
    // already locked (so concurrent workers each get a different run instead of blocking), ROWLOCK keeps the lock
    // granular. Because it is one statement it is atomic on its own - no surrounding transaction is needed - which
    // is exactly what makes it safe for many workers to call at once.
    //
    // The leading SET pins READ COMMITTED for this batch: READPAST is only valid under READ COMMITTED / REPEATABLE
    // READ, and a pooled connection can carry a leftover SERIALIZABLE level from a prior transaction (SQL Server
    // does not reliably reset the session isolation level on connection reuse), which would otherwise make the hint
    // illegal. READ COMMITTED is also the engine default, so this only ever restores it.
    // {POOL_PREDICATE} is replaced with a parameterized pool filter (the pool NAMES are bound as parameters, never
    // interpolated, so the IN list is injection-safe).
    private const string ClaimSqlTemplate = """
        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
        UPDATE [catalog].[Run]
        SET [Status] = @running, [ClaimedByNode] = @node, [StartUtc] = @now
        OUTPUT inserted.[RunId]
        WHERE [RunId] = (
            SELECT TOP (1) [RunId] FROM [catalog].[Run] WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE [Status] = @queued AND {POOL_PREDICATE}
            ORDER BY [EnqueuedUtc], [RunId]);
        """;

    /// <summary>Enqueues a run: inserts a <c>queued</c> <see cref="CatalogRun"/> row and returns its newly minted
    /// (time-ordered) id. The caller hands that id back to the trigger's caller, and the run is recorded under it,
    /// so <c>GET /runs/{id}</c> reflects the run from the moment it is queued.
    /// <para>
    /// Commit pinning: an explicit <see cref="RunEnqueueRequest.CommitSha"/> is honored verbatim. When it is
    /// omitted, the run is pinned to the repo's last successfully synced commit (the managed-sync source's
    /// <see cref="CatalogRepoSource.LastSyncedSha"/>), so the version that executes is exactly the version the
    /// catalog reflects, and ANY node in the fleet can materialize and run it, whether or not it holds a local
    /// synced copy. The version decision is therefore made centrally, at enqueue time, while the content itself
    /// still travels through git (workers materialize the commit once and cache it). Only when no synced commit is
    /// resolvable (the repo was synced from a local path with no remote, or has no managed source) does the run
    /// stay unpinned and fall back to the executing node's locally synced copy.
    /// </para></summary>
    public static Task<Guid> EnqueueAsync(
        CatalogDbContext catalog, RunEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FlowName);

        var runId = Guid.CreateVersion7();
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var commitSha = string.IsNullOrWhiteSpace(request.CommitSha)
                ? await ResolveSyncedShaAsync(catalog, request.RepoId, ct).ConfigureAwait(false)
                : request.CommitSha.Trim();

            catalog.Runs.Add(new CatalogRun
            {
                RunId = runId,
                PipelineId = CatalogIdentity.Pipeline(request.RepoId, request.FlowName),
                RepoId = request.RepoId,
                FlowName = request.FlowName,
                FlowKind = string.IsNullOrWhiteSpace(request.FlowKind) ? "unknown" : request.FlowKind,
                TargetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim(),
                CommitSha = commitSha,
                Status = RunStatuses.Queued,
                EnqueuedUtc = nowUtc,
                // Until the run finishes there is no artifact; seed WrittenUtc with the enqueue time so the run
                // sorts naturally in the (WrittenUtc-ordered) runs list, then completion overwrites it.
                WrittenUtc = nowUtc,
                Success = false,
            });
            return runId;
        }, ct);
    }

    /// <summary>
    /// The commit an unpinned enqueue defaults to: the repo's managed-sync source's last successfully synced SHA.
    /// The repo row and its source row are joined by name, which is the managed-sync invariant (the sync records
    /// the repo under the source's name; see RepoSyncService). The pin is only usable when the repo has a remote
    /// URL for workers to materialize from, so a repo synced from a bare local path never produces a pin a worker
    /// could not honor. Runs inside the enqueue transaction, so the pin and the queued row are one consistent
    /// snapshot.
    /// </summary>
    private static async Task<string?> ResolveSyncedShaAsync(
        CatalogDbContext catalog, Guid repoId, CancellationToken ct)
    {
        var sha = await (
                from repo in catalog.Repos.AsNoTracking()
                join source in catalog.RepoSources.AsNoTracking() on repo.Name equals source.Name
                where repo.Id == repoId
                      && repo.RemoteUrl != null && repo.RemoteUrl != ""
                      && source.LastSyncedSha != null && source.LastSyncedSha != ""
                select source.LastSyncedSha)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(sha) ? null : sha.Trim();
    }

    /// <summary>Atomically claims the oldest queued run this node is eligible for, flipping it to <c>running</c> and
    /// returning its id, or null when there is none. Eligibility: an untargeted run (no pool) is claimable by any
    /// node; a pooled run only by a node that serves that pool (<paramref name="pools"/>). Safe to call
    /// concurrently from many workers: each claim takes a different run (or none).</summary>
    public static async Task<Guid?> ClaimNextAsync(
        CatalogDbContext catalog, string node, IReadOnlyList<string> pools, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(pools);

        // A worker with no pools claims only untargeted runs; a pooled worker also claims runs routed to one of its
        // pools. The pool names are bound as parameters (only the @poolN placeholders are interpolated), so the IN
        // list cannot be an injection vector.
        var poolPredicate = "[TargetPool] IS NULL";
        if (pools.Count > 0)
        {
            var placeholders = string.Join(", ", pools.Select((_, i) => $"@pool{i}"));
            poolPredicate = $"([TargetPool] IS NULL OR [TargetPool] IN ({placeholders}))";
        }

        var sql = ClaimSqlTemplate.Replace("{POOL_PREDICATE}", poolPredicate, StringComparison.Ordinal);

        var strategy = catalog.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var connection = catalog.Database.GetDbConnection();
            await catalog.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                // Enlist in the context's current transaction if one is open (there is none on the standalone claim
                // path, but this keeps the command correct if a caller ever wraps it).
                if (catalog.Database.CurrentTransaction is { } tx)
                {
                    command.Transaction = tx.GetDbTransaction();
                }

                AddParameter(command, "@running", RunStatuses.Running);
                AddParameter(command, "@queued", RunStatuses.Queued);
                AddParameter(command, "@node", node);
                AddParameter(command, "@now", nowUtc);
                for (var i = 0; i < pools.Count; i++)
                {
                    AddParameter(command, $"@pool{i}", pools[i]);
                }

                var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return scalar is Guid claimed ? claimed : (Guid?)null;
            }
            finally
            {
                await catalog.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Records a claimed run's outcome from its on-disk <c>run.json</c>: copies the result fields onto the
    /// existing row, flips it to the terminal <c>succeeded</c>/<c>failed</c> state, and inserts the drill-down
    /// detail. If the artifact is missing or corrupt the run is still moved to <c>failed</c> (with the reason) so it
    /// never lingers in <c>running</c>. Returns true when recorded from a valid artifact.</summary>
    public static Task<bool> CompleteFromArtifactAsync(
        CatalogDbContext catalog, Guid runId, Guid repoId, string runJsonPath, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(runJsonPath);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            // Load WITH tracking explicitly: this is a read-modify-write, and the control plane's context defaults
            // to NoTracking (it is mostly a read model), under which a loaded entity's mutations would be silently
            // dropped by SaveChanges. AsTracking() overrides that default for this update.
            var existing = await catalog.Runs.AsTracking()
                .FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
            string? readError = null;
            try
            {
                var length = new FileInfo(runJsonPath).Length;
                if (length > CatalogSync.MaxRunJsonBytes)
                {
                    readError = $"the run artifact is {length} bytes, over the {CatalogSync.MaxRunJsonBytes}-byte limit";
                }
                else
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(runJsonPath, ct).ConfigureAwait(false));
                    var projected = CatalogProjection.RunFromJson(document.RootElement, repoId);
                    if (projected is null)
                    {
                        readError = "the run artifact is missing required fields";
                    }
                    else
                    {
                        var target = existing ?? new CatalogRun { RunId = runId };
                        ApplyCompletion(target, projected, nowUtc);
                        if (existing is null)
                        {
                            catalog.Runs.Add(target);
                        }

                        CatalogSync.AddRunDetail(catalog, document.RootElement, runId, repoId);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                readError = SecretHygiene.RedactedMessage(ex.Message);
            }

            // The artifact could not be read: still drive the run to a terminal state so it is never stuck.
            if (existing is not null)
            {
                existing.Status = RunStatuses.Failed;
                existing.Success = false;
                existing.EndUtc = nowUtc;
                existing.WrittenUtc = nowUtc;
                existing.Error = $"the run executed but its result could not be recorded: {readError}.";
            }

            return false;
        }, ct);
    }

    /// <summary>Drives a run to <c>failed</c> with a reason when execution could not even produce an artifact (the
    /// flow file was missing, failed to load, or the worker threw): a no-op if the run is already terminal, so a
    /// late failure never overwrites a recorded success.</summary>
    public static Task FailAsync(
        CatalogDbContext catalog, Guid runId, string error, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.Runs
            .Where(r => r.RunId == runId && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Failed)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, error)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct);
    }

    /// <summary>Cancels a run if (and only if) it is still queued. Atomic: a run that is claimed for execution
    /// between the check and the update is reported as not-cancellable rather than cancelled out from under a
    /// worker.</summary>
    public static async Task<CancelOutcome> CancelAsync(
        CatalogDbContext catalog, Guid runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var cancelled = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Cancelled)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (cancelled > 0)
        {
            return CancelOutcome.Cancelled;
        }

        var exists = await catalog.Runs.AsNoTracking().AnyAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
        return exists ? CancelOutcome.NotCancellable : CancelOutcome.NotFound;
    }

    /// <summary>Requeues runs left <c>running</c> by this node: on worker startup they are orphans from a previous
    /// incarnation that stopped mid-run, so they are reset to <c>queued</c> to be picked up again. Returns the
    /// number recovered. (Reclaiming another live node's stale runs by claim age is a multi-node-phase concern.)</summary>
    public static Task<int> RecoverStuckRunningAsync(CatalogDbContext catalog, string node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        return catalog.Runs
            .Where(r => r.Status == RunStatuses.Running && r.ClaimedByNode == node)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Queued)
                .SetProperty(r => r.ClaimedByNode, (string?)null), ct);
    }

    private static void ApplyCompletion(CatalogRun target, CatalogRun projected, DateTime nowUtc)
    {
        // Identity fields are the same whether the row was enqueued or is being inserted fresh (RunFromJson derives
        // PipelineId from repo + flow name, exactly as enqueue did); the queue-only fields (EnqueuedUtc,
        // ClaimedByNode) and the claim's StartUtc are preserved.
        target.PipelineId = projected.PipelineId;
        target.RepoId = projected.RepoId;
        target.FlowName = projected.FlowName;
        target.FlowKind = projected.FlowKind;
        target.Success = projected.Success;
        target.Status = projected.Success ? RunStatuses.Succeeded : RunStatuses.Failed;
        target.SchemaVersion = projected.SchemaVersion;
        target.WrittenUtc = projected.WrittenUtc == default ? nowUtc : projected.WrittenUtc;
        target.StartUtc = projected.StartUtc ?? target.StartUtc;
        target.EndUtc = projected.EndUtc ?? nowUtc;
        target.DurationSeconds = projected.DurationSeconds;
        target.RowsLoaded = projected.RowsLoaded;
        target.RowsInserted = projected.RowsInserted;
        target.RowsUpdated = projected.RowsUpdated;
        target.RowsDeleted = projected.RowsDeleted;
        target.Error = projected.Error;
        target.Host = projected.Host ?? target.Host;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
