using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Catalog;

/// <summary>What to enqueue: references only (never a secret). The flow is addressed by repo + name; an optional
/// pool routes it to eligible nodes, and an optional commit SHA pins it to an exact git version the node
/// materializes. A null <see cref="CommitSha"/> is not "unpinned" but "default": enqueueing pins the run to the
/// repo's last synced commit when one is known (see <see cref="RunQueueStore.EnqueueAsync"/>), so any node in the
/// fleet can execute it. Enqueueing also snapshots the pipeline's exact YAML into the content-addressed
/// <see cref="CatalogFlowVersion"/> store and stamps the run with its hash, so the executing node loads the
/// document from the catalog instead of cloning the repo; the commit pin remains the audit trail and the
/// fallback. <see cref="Parameters"/> carries the per-run substitution parameters (the built-in backfill: full
/// load, window, file pattern), validated at the trust boundary and recorded on the run row so every backfill is
/// auditable. Bundled into one request so the optional references can never be passed in the wrong order.</summary>
public sealed record RunEnqueueRequest(
    Guid RepoId, string FlowName, string FlowKind, string? TargetPool = null, string? CommitSha = null,
    RunParameters? Parameters = null);

/// <summary>What to enqueue as one multi-flow run group (a Node or Batch execution): the resolved, ordered member
/// flows (with their waves) plus the shared routing. Every member is enqueued under one <see cref="RunGroupModes"/>
/// header and gated by wave, so a dependency never runs before what it depends on.
/// <para>A node backfill carries per-member parameters through <paramref name="MemberParameters"/> (keyed by flow
/// name): the caller decides, per member, whether it takes the backfill window (the anchor and every window-honoring
/// descendant, so each layer re-reads the same historical slice) or <see cref="RunParameters.ReprocessFromSourceMin"/>
/// (a relational descendant, so the back-dated rows an upstream flow re-lands are re-pulled instead of stopping below
/// the target's high-water mark). A member absent from the map runs with default parameters, so an ordinary group (or
/// a schedule fire) passes no map and every member runs as defined.</para></summary>
public sealed record RunGroupEnqueueRequest(
    Guid RepoId, string Mode, string Anchor, IReadOnlyList<RunScopeMember> Members,
    string? TargetPool = null, string? CommitSha = null,
    IReadOnlyDictionary<string, RunParameters>? MemberParameters = null);

/// <summary>The outcome of enqueuing a group: the new group id and the ids of every member run, in wave order.</summary>
public sealed record RunGroupEnqueueResult(Guid GroupId, IReadOnlyList<Guid> RunIds);

/// <summary>The outcome of cancelling a run group: whether the group existed, how many queued members were cancelled
/// outright, and how many running members had a cancel request stamped (the latter drives a worker nudge).</summary>
public sealed record GroupCancelResult(bool Found, int CancelledQueued, int RequestedRunning);

/// <summary>The result of a cancel request, so the API can answer 200 / 202 / 404 / 409 precisely.</summary>
public enum CancelOutcome
{
    /// <summary>The run was queued and is now cancelled outright (it never started).</summary>
    Cancelled,

    /// <summary>The run was already running, so a cancel was requested: the owning node will abort the in-flight
    /// statement and record the run cancelled. The cancel is durable but asynchronous, hence a distinct outcome.</summary>
    CancelRequested,

    /// <summary>No run with that id exists.</summary>
    NotFound,

    /// <summary>The run exists but is already finished (terminal), so there is nothing to cancel.</summary>
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
    //
    // The group-gating clause enforces wave order within a run group without any external coordinator: a member of a
    // group is claimable only once every same-group member in a LOWER wave is terminal (no sibling with a smaller
    // GroupWave is still queued or running). A standalone run (GroupId IS NULL) short-circuits the clause and is
    // claimable exactly as before. Because a blocked member simply is not selected (rather than locked), READPAST
    // still lets a worker move straight to the next eligible run - a not-yet-ready wave never stalls the queue.
    //
    // The pipeline-gating clause serializes executions of the SAME flow: a run is claimable only while no other run
    // of its pipeline is executing, so a double-trigger (or a schedule firing while the previous run is still going)
    // queues behind the running one instead of racing it. This is load-bearing for ingestion: each flow stages
    // through one canonical work table ([raw].[<schema>_<table>_<flowId>]), which two overlapping executions of the
    // flow would clobber. Like the group gate, a blocked duplicate is simply not selected, so the worker moves on to
    // the next eligible run; node-restart recovery (RecoverStuckRunningAsync) requeues orphaned running rows, so a
    // crashed run cannot wedge its pipeline.
    private const string ClaimSqlTemplate = """
        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
        UPDATE [catalog].[Run]
        SET [Status] = @running, [ClaimedByNode] = @node, [StartUtc] = @now
        OUTPUT inserted.[RunId]
        WHERE [RunId] = (
            SELECT TOP (1) r.[RunId] FROM [catalog].[Run] AS r WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE r.[Status] = @queued AND {POOL_PREDICATE}
              AND (r.[GroupId] IS NULL OR NOT EXISTS (
                  SELECT 1 FROM [catalog].[Run] AS s
                  WHERE s.[GroupId] = r.[GroupId] AND s.[GroupWave] < r.[GroupWave]
                    AND s.[Status] IN (@queued, @running)))
              AND NOT EXISTS (
                  SELECT 1 FROM [catalog].[Run] AS p
                  WHERE p.[PipelineId] = r.[PipelineId] AND p.[Status] = @running)
            ORDER BY r.[EnqueuedUtc], r.[RunId]);
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
    /// </para>
    /// <para>
    /// Content snapshot: alongside the pin, the pipeline's current YAML is snapshotted into the content-addressed
    /// <see cref="CatalogFlowVersion"/> store and the run is stamped with its hash, so the executing node reads
    /// the document from the catalog and needs no git access at all for the common case. A missing pipeline row,
    /// empty stored YAML, or a flow that embeds a literal credential (whose stored YAML is the redacted form, not
    /// the committed bytes) is not snapshotted: the run keeps the git materialization path above.
    /// </para></summary>
    public static async Task<Guid> EnqueueAsync(
        CatalogDbContext catalog, RunEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FlowName);

        var parameters = request.Parameters ?? RunParameters.None;
        parameters.Validate();

        var runId = Guid.CreateVersion7();
        var pipelineId = CatalogIdentity.Pipeline(request.RepoId, request.FlowName);

        // Resolve the commit this run executes and whether the catalog snapshot is a faithful copy of it. The
        // snapshot in CatalogFlowVersion holds the CURRENTLY SYNCED version (what CatalogPipeline.Yaml reflects),
        // so it may only serve a run that executes that same commit: an empty (default) pin, or an explicit pin
        // equal to the synced commit. A pin to any OTHER commit must run those exact bytes, which the catalog does
        // not hold, so it is left unstamped and takes the git materialization path, preserving commit-pin
        // reproducibility.
        var (commitSha, snapshotMatchesCommit) =
            await ResolveCommitForSnapshotAsync(catalog, request.RepoId, request.CommitSha, ct).ConfigureAwait(false);
        var flowVersionHash = snapshotMatchesCommit
            ? await EnsureFlowVersionAsync(catalog, pipelineId, nowUtc, ct).ConfigureAwait(false)
            : null;

        return await CatalogTransaction.InSerializableAsync(catalog, () =>
        {
            catalog.Runs.Add(new CatalogRun
            {
                RunId = runId,
                PipelineId = pipelineId,
                RepoId = request.RepoId,
                FlowName = request.FlowName,
                FlowKind = string.IsNullOrWhiteSpace(request.FlowKind) ? "unknown" : request.FlowKind,
                TargetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim(),
                CommitSha = commitSha,
                FlowVersionHash = flowVersionHash,
                FullLoad = parameters.FullLoad,
                BackfillFrom = parameters.BackfillFrom,
                BackfillTo = parameters.BackfillTo,
                FilePattern = string.IsNullOrWhiteSpace(parameters.FilePattern) ? null : parameters.FilePattern.Trim(),
                AssertionsOnly = parameters.AssertionsOnly,
                ReprocessFromSourceMin = parameters.ReprocessFromSourceMin,
                Status = RunStatuses.Queued,
                EnqueuedUtc = nowUtc,
                // Until the run finishes there is no artifact; seed WrittenUtc with the enqueue time so the run
                // sorts naturally in the (WrittenUtc-ordered) runs list, then completion overwrites it.
                WrittenUtc = nowUtc,
                Success = false,
            });
            return Task.FromResult(runId);
        }, ct);
    }

    /// <summary>Enqueues a whole run group (a Node or Batch execution) atomically: inserts one
    /// <see cref="CatalogRunGroup"/> header and one <c>queued</c> <see cref="CatalogRun"/> per member, each stamped
    /// with the shared <see cref="CatalogRun.GroupId"/> and its own <see cref="CatalogRun.GroupWave"/> so the claim
    /// runs them in wave order. Every member is pinned to the same resolved commit (so the whole set executes one
    /// consistent version) and carries default run parameters (backfill is single-flow only). Returns the group id
    /// and the member run ids in wave order. Members are validated non-empty by the caller (an empty scope is a
    /// request error, not something to enqueue).</summary>
    public static async Task<RunGroupEnqueueResult> EnqueueGroupAsync(
        CatalogDbContext catalog, RunGroupEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Anchor);
        if (request.Members is not { Count: > 0 })
        {
            throw new ArgumentException("A run group must have at least one member flow.", nameof(request));
        }

        var groupId = Guid.CreateVersion7();
        var targetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim();

        // Per-member parameters for a node backfill (the caller routed each member to the window or to
        // reprocess-from-source-min); a member absent from the map runs with defaults. Each is validated here, at the
        // enqueue trust boundary, exactly as a single run's parameters are. An ordinary node run or a schedule fire
        // passes no map, so every member resolves to default parameters.
        RunParameters MemberParameters(RunScopeMember member)
            => request.MemberParameters?.GetValueOrDefault(member.FlowName) ?? RunParameters.None;
        if (request.MemberParameters is not null)
        {
            foreach (var member in request.Members)
            {
                MemberParameters(member).Validate();
            }
        }

        // Resolve the shared commit and whether the catalog snapshot faithfully copies it (see the single-run
        // enqueue). Only when it does is each member's YAML staged into the content-addressed store before the run
        // transaction, so the serializable transaction only inserts run rows. Distinct pipelines are staged once;
        // members that share content (a group fanning out one version) reuse the row the first EnsureFlowVersionAsync
        // committed. A pin to a non-synced commit leaves every member unstamped, on the git materialization path.
        var (commitSha, snapshotMatchesCommit) =
            await ResolveCommitForSnapshotAsync(catalog, request.RepoId, request.CommitSha, ct).ConfigureAwait(false);
        var flowVersionByPipeline = new Dictionary<Guid, string?>();
        if (snapshotMatchesCommit)
        {
            foreach (var member in request.Members)
            {
                var pipelineId = CatalogIdentity.Pipeline(request.RepoId, member.FlowName);
                if (!flowVersionByPipeline.ContainsKey(pipelineId))
                {
                    flowVersionByPipeline[pipelineId] =
                        await EnsureFlowVersionAsync(catalog, pipelineId, nowUtc, ct).ConfigureAwait(false);
                }
            }
        }

        return await CatalogTransaction.InSerializableAsync(catalog, () =>
        {
            catalog.RunGroups.Add(new CatalogRunGroup
            {
                GroupId = groupId,
                RepoId = request.RepoId,
                Mode = request.Mode,
                Anchor = request.Anchor,
                MemberCount = request.Members.Count,
                CommitSha = commitSha,
                EnqueuedUtc = nowUtc,
            });

            var runIds = new List<Guid>(request.Members.Count);
            foreach (var member in request.Members)
            {
                var runId = Guid.CreateVersion7();
                runIds.Add(runId);
                var pipelineId = CatalogIdentity.Pipeline(request.RepoId, member.FlowName);
                var memberParameters = MemberParameters(member);
                catalog.Runs.Add(new CatalogRun
                {
                    RunId = runId,
                    PipelineId = pipelineId,
                    RepoId = request.RepoId,
                    FlowName = member.FlowName,
                    FlowKind = string.IsNullOrWhiteSpace(member.FlowKind) ? "unknown" : member.FlowKind,
                    TargetPool = targetPool,
                    CommitSha = commitSha,
                    FlowVersionHash = flowVersionByPipeline.GetValueOrDefault(pipelineId),
                    GroupId = groupId,
                    // A negative (uncomputed) wave collapses to 0 so an un-analyzed set runs as one parallel wave.
                    GroupWave = member.Wave < 0 ? 0 : member.Wave,
                    FullLoad = memberParameters.FullLoad,
                    BackfillFrom = memberParameters.BackfillFrom,
                    BackfillTo = memberParameters.BackfillTo,
                    FilePattern = string.IsNullOrWhiteSpace(memberParameters.FilePattern) ? null : memberParameters.FilePattern.Trim(),
                    AssertionsOnly = memberParameters.AssertionsOnly,
                    ReprocessFromSourceMin = memberParameters.ReprocessFromSourceMin,
                    Status = RunStatuses.Queued,
                    EnqueuedUtc = nowUtc,
                    WrittenUtc = nowUtc,
                    Success = false,
                });
            }

            return Task.FromResult(new RunGroupEnqueueResult(groupId, runIds));
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

    /// <summary>
    /// Resolves the commit a run executes from its request, and whether the catalog's YAML snapshot faithfully
    /// copies that commit. An explicit <paramref name="requestedSha"/> is honored verbatim; an empty one defaults to
    /// the repo's last synced commit (see <see cref="ResolveSyncedShaAsync"/>). The snapshot in
    /// <see cref="CatalogFlowVersion"/> only ever holds the currently synced version, so it matches the run when the
    /// run executes that synced commit: an empty pin, or an explicit pin equal to the synced SHA. A pin to any other
    /// commit does not match, and its run must materialize those exact bytes from git rather than run the snapshot.
    /// </summary>
    private static async Task<(string? CommitSha, bool SnapshotMatchesCommit)> ResolveCommitForSnapshotAsync(
        CatalogDbContext catalog, Guid repoId, string? requestedSha, CancellationToken ct)
    {
        var syncedSha = await ResolveSyncedShaAsync(catalog, repoId, ct).ConfigureAwait(false);
        var explicitSha = string.IsNullOrWhiteSpace(requestedSha) ? null : requestedSha.Trim();
        var commitSha = explicitSha ?? syncedSha;
        var snapshotMatchesCommit = explicitSha is null
            || string.Equals(explicitSha, syncedSha, StringComparison.OrdinalIgnoreCase);
        return (commitSha, snapshotMatchesCommit);
    }

    /// <summary>
    /// Snapshots the pipeline's current YAML into the content-addressed <see cref="CatalogFlowVersion"/> store and
    /// returns its hash, staging the version row when this content has not been seen before (same hash = same bytes,
    /// so a hundred runs at one commit share one row). Returns null, leaving the run on the git materialization
    /// path, when no trustworthy snapshot exists: the pipeline row is missing (the flow left the catalog between
    /// resolution and enqueue), its stored YAML is empty, or the YAML carries an embedded literal credential. In
    /// that last case the stored text is the redacted form, not the committed bytes, so executing it would silently
    /// run a different document; such a flow (already loudly warned by the sync) keeps cloning git until it is fixed
    /// to use ${...} references.
    /// <para>
    /// Deliberately staged in its own short save, OUTSIDE the caller's serializable run transaction: the version
    /// store is append-only immutable content, so committing it independently is correct, and it keeps the
    /// content-addressed table off the serializable transaction's lock footprint (two concurrent enqueues of the
    /// same brand-new version would otherwise take conflicting range locks and deadlock). A concurrent staging of
    /// the identical version races benignly: the loser catches the duplicate-key and treats the existing row, whose
    /// bytes are identical, as the staged one. An orphaned version row (its run transaction later fails) is
    /// harmless and reused by the next enqueue of that content.
    /// </para>
    /// </summary>
    private static async Task<string?> EnsureFlowVersionAsync(
        CatalogDbContext catalog, Guid pipelineId, DateTime nowUtc, CancellationToken ct)
    {
        var pipeline = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => new { p.ContentHash, p.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null
            || string.IsNullOrWhiteSpace(pipeline.ContentHash)
            || string.IsNullOrWhiteSpace(pipeline.Yaml)
            || SecretHygiene.LooksLikeEmbeddedSecret(pipeline.Yaml))
        {
            return null;
        }

        var exists = await catalog.FlowVersions.AsNoTracking()
            .AnyAsync(v => v.ContentHash == pipeline.ContentHash, ct).ConfigureAwait(false);
        if (!exists)
        {
            catalog.FlowVersions.Add(new CatalogFlowVersion
            {
                ContentHash = pipeline.ContentHash,
                Yaml = pipeline.Yaml,
                FirstSeenUtc = nowUtc,
            });
            try
            {
                await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // A concurrent enqueue staged this identical (content-addressed) version first; the row exists with
                // the same bytes, which is exactly the goal. Detach the failed add so the context stays clean for
                // the run transaction that follows.
                var entry = catalog.Entry(catalog.FlowVersions.Local.First(v => v.ContentHash == pipeline.ContentHash));
                entry.State = EntityState.Detached;
            }
        }

        return pipeline.ContentHash;
    }

    /// <summary>Atomically claims the oldest queued run this node is eligible for, flipping it to <c>running</c> and
    /// returning its id, or null when there is none. Eligibility: an untargeted run (no pool) is claimable by any
    /// node; a pooled run only by a node that serves that pool (<paramref name="pools"/>); a run whose pipeline
    /// already has a running execution waits its turn (same-flow runs never overlap, protecting the flow's canonical
    /// staging table). Safe to call concurrently from many workers: each claim takes a different run (or none).</summary>
    public static async Task<Guid?> ClaimNextAsync(
        CatalogDbContext catalog, string node, IReadOnlyList<string> pools, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(pools);

        // A worker with no pools claims only untargeted runs; a pooled worker also claims runs routed to one of its
        // pools. The pool names are bound as parameters (only the @poolN placeholders are interpolated), so the IN
        // list cannot be an injection vector.
        var poolPredicate = "r.[TargetPool] IS NULL";
        if (pools.Count > 0)
        {
            var placeholders = string.Join(", ", pools.Select((_, i) => $"@pool{i}"));
            poolPredicate = $"(r.[TargetPool] IS NULL OR r.[TargetPool] IN ({placeholders}))";
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

                        // The node streamed this run's statements and canonical events into the catalog live as it
                        // executed: each is an immutable, append-only row the trace stream already delivered under a
                        // stable id. Do NOT delete and re-project them - that would re-issue every row under a fresh
                        // id, and the live tail (which forwards rows past the client's id cursor) would re-stream the
                        // whole trace. Instead append only the tail the live feed did not write: nothing in the
                        // normal case (the feed captured everything), or the gap after the point a best-effort feed
                        // broke, taken from the authoritative run.json. CLI and full-sync runs have no live rows, so
                        // the whole detail is inserted. Atomic with the rest of the completion in this serializable
                        // transaction.
                        var maxEventOrdinal = await catalog.RunEvents.Where(e => e.RunId == runId)
                            .Select(e => (int?)e.Ordinal).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                        var maxStatementOrdinal = await catalog.RunStatements.Where(s => s.RunId == runId)
                            .Select(s => (int?)s.Ordinal).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                        CatalogSync.AddRunDetail(
                            catalog, document.RootElement, runId, repoId, maxEventOrdinal, maxStatementOrdinal);
                        // A failed group member strands its dependents: skip them in the same transaction so the
                        // completion and its consequences commit together (a no-op for a standalone or succeeded run).
                        if (!projected.Success)
                        {
                            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
                        }

                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                readError = SecretHygiene.RedactedMessage(ex);
            }

            // The artifact could not be read: still drive the run to a terminal state so it is never stuck.
            if (existing is not null)
            {
                existing.Status = RunStatuses.Failed;
                existing.Success = false;
                existing.EndUtc = nowUtc;
                existing.WrittenUtc = nowUtc;
                existing.Error = $"the run executed but its result could not be recorded: {readError}.";
                // An unrecordable run is still a failed group member: strand its dependents like any other failure.
                await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
            }

            return false;
        }, ct);
    }

    /// <summary>Drives a run to <c>failed</c> with a reason when execution could not even produce an artifact (the
    /// flow file was missing, failed to load, or the worker threw): a no-op if the run is already terminal, so a
    /// late failure never overwrites a recorded success.</summary>
    public static async Task FailAsync(
        CatalogDbContext catalog, Guid runId, string error, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var failed = await catalog.Runs
            .Where(r => r.RunId == runId && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Failed)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, error)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);

        if (failed > 0)
        {
            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels a run, honoring its lifecycle. A still-queued run is cancelled outright (it never ran). A
    /// run already <c>running</c> cannot be cancelled out from under its worker here; instead a durable cancel
    /// request is stamped (<see cref="CatalogRun.CancelRequestedUtc"/>) for the owning node to observe, abort the
    /// in-flight statement, and record the run cancelled (see <see cref="ListCancelRequestedAsync"/> /
    /// <see cref="CancelRunningAsync"/>). Each step is a single atomic conditional update, so a run that is claimed
    /// between the queued check and the running check is caught by the second step rather than lost. Requesting a
    /// cancel on a run that already has one pending is idempotent (still <see cref="CancelOutcome.CancelRequested"/>).</summary>
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
            // Cancelling a queued group member is a non-success terminal too: its dependents in the group can no
            // longer run, so skip them (a no-op for a standalone run).
            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
            return CancelOutcome.Cancelled;
        }

        // Still-running: record the request for the owning node. Stamp CancelRequestedUtc only when it is not yet
        // set, so the request reflects when the operator first asked (a repeated click does not keep moving it).
        var requested = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running && r.CancelRequestedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequestedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (requested > 0)
        {
            return CancelOutcome.CancelRequested;
        }

        // No queued or freshly-running row updated: either it is running with a request already pending (idempotent
        // success), it is already terminal (nothing to cancel), or it does not exist.
        var state = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => (string?)r.Status)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return state switch
        {
            null => CancelOutcome.NotFound,
            RunStatuses.Running => CancelOutcome.CancelRequested,
            _ => CancelOutcome.NotCancellable,
        };
    }

    /// <summary>Cancels a whole run group as a unit: every still-<c>queued</c> member is cancelled outright and every
    /// member already <c>running</c> gets a durable cancel request stamped for its owning node to honor, exactly as
    /// the single-run <see cref="CancelAsync"/> does. No per-member dependent-skipping is needed here: the entire
    /// group is being cancelled, so there is nothing left to strand. Returns whether the group existed and how many
    /// members were affected, so the endpoint can answer 404 for an unknown group and the dispatcher can nudge the
    /// worker when a running member must observe its request.</summary>
    public static async Task<GroupCancelResult> CancelGroupAsync(
        CatalogDbContext catalog, Guid groupId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var found = await catalog.RunGroups.AsNoTracking()
            .AnyAsync(g => g.GroupId == groupId, ct).ConfigureAwait(false);
        if (!found)
        {
            return new GroupCancelResult(false, 0, 0);
        }

        var cancelledQueued = await catalog.Runs
            .Where(r => r.GroupId == groupId && r.Status == RunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Cancelled)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);

        var requestedRunning = await catalog.Runs
            .Where(r => r.GroupId == groupId && r.Status == RunStatuses.Running && r.CancelRequestedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequestedUtc, nowUtc), ct)
            .ConfigureAwait(false);

        return new GroupCancelResult(true, cancelledQueued, requestedRunning);
    }

    /// <summary>The ids of runs this node is executing that an operator has asked to cancel: <c>running</c>, claimed
    /// by <paramref name="node"/>, with a pending <see cref="CatalogRun.CancelRequestedUtc"/>. The worker polls this
    /// to trip the matching run's cancellation token. Scoped to the node so a worker only ever cancels its own
    /// in-flight work.</summary>
    public static Task<List<Guid>> ListCancelRequestedAsync(
        CatalogDbContext catalog, string node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        return catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running && r.ClaimedByNode == node && r.CancelRequestedUtc != null)
            .Select(r => r.RunId)
            .ToListAsync(ct);
    }

    /// <summary>Records a running run as <c>cancelled</c> after its owning node has aborted the in-flight statement.
    /// Conditional on the run still being <c>running</c>, so a run that finished on its own (succeeded/failed) in the
    /// same instant is never overwritten by a late cancel.</summary>
    public static async Task<int> CancelRunningAsync(
        CatalogDbContext catalog, Guid runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var cancelled = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Cancelled)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, "The run was cancelled by an operator while executing.")
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);

        if (cancelled > 0)
        {
            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
        }

        return cancelled;
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

    /// <summary>Fails runs left <c>running</c> by a node that is no longer alive. A run is an orphan when its
    /// <c>ClaimedByNode</c> has no fleet heartbeat at or after <paramref name="staleBefore"/> (its registry row is
    /// absent or its last-seen is older than the cutoff), or when no claimant is recorded at all: in every case the
    /// process that was executing it is gone and no outcome will ever be recorded, so the run would otherwise sit
    /// <c>running</c> forever and block every future run of its pipeline (the claim's pipeline gate). Unlike
    /// <see cref="RecoverStuckRunningAsync"/>, which requeues a node's OWN restart orphans by name, this reclaims any
    /// node's orphans by liveness, so a crashed pod that never returns under the same name is still cleared. Each run
    /// is failed with a conditional update guarded on it still being <c>running</c>, so a run its real node completes
    /// in the same instant is never overwritten, and concurrent reapers on multiple control-plane replicas are
    /// idempotent; a failed group member skips its still-queued dependents, exactly as an operator cancel does.
    /// Returns the number failed. The liveness signal is only safe to act on because a node heartbeats on a cadence
    /// independent of its draining (<c>RunWorker.HeartbeatLoopAsync</c>), so a busy node is never mistaken for a dead
    /// one; the caller sets <paramref name="staleBefore"/> comfortably older than that cadence.</summary>
    public static async Task<int> ReapOrphanedRunningAsync(
        CatalogDbContext catalog, DateTime staleBefore, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // Candidate orphans: running rows with no live node backing them, resolved server-side as one indexed
        // anti-join against the fleet registry. A healthy fleet returns nothing, so the steady-state cost is a
        // single cheap read.
        var orphans = await catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running)
            .Where(r => r.ClaimedByNode == null
                || !catalog.Nodes.Any(n => n.Name == r.ClaimedByNode && n.LastSeenUtc >= staleBefore))
            .Select(r => new { r.RunId, r.ClaimedByNode })
            .ToListAsync(ct).ConfigureAwait(false);
        if (orphans.Count == 0)
        {
            return 0;
        }

        var failed = 0;
        foreach (var orphan in orphans)
        {
            var node = orphan.ClaimedByNode ?? "(unclaimed)";
            var error =
                $"Run orphaned: its claiming node '{node}' stopped heartbeating, so the executing process is gone "
                + "and no outcome will ever be recorded. The control-plane orphan reaper failed it to release the "
                + "run and its pipeline gate; re-trigger the flow to run it again.";
            var updated = await catalog.Runs
                .Where(r => r.RunId == orphan.RunId && r.Status == RunStatuses.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatuses.Failed)
                    .SetProperty(r => r.Success, false)
                    .SetProperty(r => r.Error, error)
                    .SetProperty(r => r.EndUtc, nowUtc)
                    .SetProperty(r => r.WrittenUtc, nowUtc), ct)
                .ConfigureAwait(false);
            if (updated > 0)
            {
                await SkipGroupDescendantsAsync(catalog, orphan.RunId, nowUtc, ct).ConfigureAwait(false);
                failed++;
            }
        }

        return failed;
    }

    /// <summary>When a group member reaches a non-success terminal state (failed / cancelled), marks every member
    /// that transitively depends on it and is still <c>queued</c> as <c>skipped</c>: a broken upstream is never fed
    /// downstream, while independent branches of the group keep running. A no-op for a standalone run (no group), a
    /// succeeded run, or a run whose dependents have all already started. Idempotent (guarded on <c>queued</c>), so
    /// two failures in the same group each skip their own cone without fighting. The reachable set is computed over
    /// the group's own members only, so a dependent outside this run group is never touched.</summary>
    private static async Task SkipGroupDescendantsAsync(
        CatalogDbContext catalog, Guid runId, DateTime nowUtc, CancellationToken ct)
    {
        var run = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => new { r.GroupId, r.PipelineId, r.RepoId, r.FlowName, r.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is null || run.GroupId is not { } groupId || run.RepoId is not { } repoId
            || run.Status == RunStatuses.Succeeded)
        {
            return;
        }

        var memberIds = (await catalog.Runs.AsNoTracking()
                .Where(r => r.GroupId == groupId)
                .Select(r => r.PipelineId)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet();

        var edges = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId)
            .Select(d => new { d.FromPipelineId, d.ToPipelineId })
            .ToListAsync(ct).ConfigureAwait(false);

        var outgoing = new Dictionary<Guid, List<Guid>>();
        foreach (var edge in edges)
        {
            // Only edges wholly inside this group matter: a dependency on a flow that is not part of the run cannot
            // be satisfied or skipped by it.
            if (!memberIds.Contains(edge.FromPipelineId) || !memberIds.Contains(edge.ToPipelineId))
            {
                continue;
            }

            if (!outgoing.TryGetValue(edge.FromPipelineId, out var to))
            {
                to = new List<Guid>();
                outgoing[edge.FromPipelineId] = to;
            }

            to.Add(edge.ToPipelineId);
        }

        var dependents = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(run.PipelineId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoing.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var next in neighbors)
            {
                if (dependents.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        if (dependents.Count == 0)
        {
            return;
        }

        var dependentIds = dependents.ToList();
        var reason = $"skipped: an upstream dependency ('{run.FlowName}') did not succeed.";
        await catalog.Runs
            .Where(r => r.GroupId == groupId && r.Status == RunStatuses.Queued && dependentIds.Contains(r.PipelineId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Skipped)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, reason)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);
    }

    private static void ApplyCompletion(CatalogRun target, CatalogRun projected, DateTime nowUtc)
    {
        // Identity fields are the same whether the row was enqueued or is being inserted fresh (RunFromJson derives
        // PipelineId from repo + flow name, exactly as enqueue did); the queue-only fields (EnqueuedUtc,
        // ClaimedByNode, the run parameters FullLoad/BackfillFrom/BackfillTo/FilePattern) and the claim's
        // StartUtc are preserved by simply not assigning them here.
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
        target.IncrementalMode = projected.IncrementalMode;
        target.IncrementalFilter = projected.IncrementalFilter;
        target.IncrementalWatermark = projected.IncrementalWatermark;
        target.IncrementalWatermarkSource = projected.IncrementalWatermarkSource;
        target.DataSetConvention = projected.DataSetConvention;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
