using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.Catalog;

/// <summary>The tally of one catalog sync pass.</summary>
public sealed record CatalogSyncResult
{
    public int PipelinesAdded { get; init; }
    public int PipelinesUpdated { get; init; }
    public int PipelinesUnchanged { get; init; }
    public int PipelinesDeactivated { get; init; }

    /// <summary>Pipelines removed because their flow genuinely left the repository (run history is kept).</summary>
    public int PipelinesDeleted { get; init; }
    public int RunsAdded { get; init; }
    public int RunsSkipped { get; init; }
    public int RunsFailed { get; init; }
    public int RunEventsAdded { get; init; }

    /// <summary>The document families the registered sync extensions reconcile (the delivery kind's mappings and
    /// snapshots), tallied across every extension.</summary>
    public int DocumentsAdded { get; init; }
    public int DocumentsUpdated { get; init; }
    public int DocumentsUnchanged { get; init; }
    public int DocumentsRemoved { get; init; }
    public int DocumentsInvalid { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The per-pass tally of run artifacts and the event rows projected from them.</summary>
internal readonly record struct RunSyncTally(int Added, int Skipped, int Failed, int Events);

/// <summary>What a per-run write-back did to the flow's pipeline row.</summary>
public enum PipelineChange
{
    None,
    Added,
    Updated,
    Unchanged,
}

/// <summary>The result of recording one just-completed run into the catalog (the self-maintaining write-back).</summary>
public sealed record RecordRunResult
{
    public PipelineChange PipelineChange { get; init; }
    public bool RunRecorded { get; init; }
    public int RunEventsAdded { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Synchronizes the shadow catalog FROM the source of truth: the git/YAML estate (the pipeline registry, mapped
/// into rows with the full definition in queryable JSON) and the on-disk <c>run.json</c> history. One direction
/// only - the database mirrors the files, never the reverse - so the catalog can always be rebuilt by re-syncing,
/// and several repos sync into one catalog for cross-repo queries. Pipelines are upserted by their stable
/// identity (ones that have left the estate are removed, keeping their run history); runs are inserted by
/// their own id, so re-syncing the same folders, or aggregating many nodes' folders, is idempotent. A pass runs
/// in two phases: the estate is collected and every document parsed exactly once up front (pure computation, no
/// transaction held), then the reconciliation writes run in one serializable transaction so two syncs of the
/// same repo cannot lose each other's updates. Secrets never rest in the catalog: a flow document is detected
/// (and warned) when it embeds a credential, and the stored YAML / definition JSON are passed through the same
/// redactor used for connection-string error text.
/// </summary>
public sealed class CatalogSync
{
    // A flow document is kilobytes; a run.json is small. These caps stop a hostile or corrupt file from
    // exhausting memory or bloating the nvarchar(max) columns. Over-limit files are skipped with a warning.
    private const long MaxYamlBytes = 16L * 1024 * 1024;
    internal const long MaxRunJsonBytes = 64L * 1024 * 1024;

    // SQL Server allows roughly 2100 parameters per command, and a keys.Contains(...) predicate can translate
    // to one parameter per key, so membership queries over report-sized key sets run in bounded chunks whose
    // results are combined. 500 keys per round trip stays far under the limit.
    private const int KeyChunkSize = 500;

    private static readonly JsonSerializerOptions DefinitionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly YamlDocumentLoader _documents;
    private readonly EstateScanner _estate;
    private readonly IReadOnlyList<ICatalogSyncExtension> _extensions;

    /// <param name="documents">The loader every present flow document is parsed with.</param>
    /// <param name="extensions">The document families registered kinds add to the sync; none by default.</param>
    public CatalogSync(YamlDocumentLoader documents, IEnumerable<ICatalogSyncExtension>? extensions = null)
    {
        _extensions = extensions?.ToList() ?? [];
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        _estate = new EstateScanner(documents);
    }

    /// <summary>One estate flow prepared for the reconciliation transaction: its redacted text, content hash and
    /// serialized definition. Prepared once per pass; the transaction (and any retry of it) stages fresh rows from
    /// this data.</summary>
    private sealed record PreparedPipeline
    {
        public required CollectedFlow Flow { get; init; }

        public required Guid Id { get; init; }

        public required string Yaml { get; init; }

        public required string ContentHash { get; init; }

        public required string DefinitionJson { get; init; }
    }

    /// <summary>One validated git-declared schedule, with the flows that joined it and where git declares it, ready
    /// to stage into the schedule and schedule-member tables.</summary>
    private sealed record PreparedSchedule(
        string Name, IReadOnlyList<string> Members, ScheduleSpec Spec, DateTime NextFireUtc,
        ScheduleDefinitionSource Definition);

    /// <summary>One run artifact awaiting insertion: the projected header row (client-keyed, so re-adding it on
    /// a transaction retry is safe) and the parsed document its event rows project from inside the transaction,
    /// retained so the file is read and parsed exactly once per pass.</summary>
    private sealed record PreparedRun(CatalogRun Run, JsonDocument Document) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    public async Task<CatalogSyncResult> SyncAsync(
        CatalogDbContext context, string estateDirectory, string repoName, string? repoRemoteUrl, DateTime nowUtc,
        IReadOnlySet<string>? excludedFlowPaths = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(estateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        var root = Path.GetFullPath(estateDirectory);
        var repoId = FlowIdentity.FromName(repoName);
        var warnings = new List<string>();

        // ---- Phase one: pure computation and reads, before any transaction. The estate is collected once and
        // each present document read and parsed once.
        var collected = _estate.Collect(root);
        warnings.AddRange(collected.Warnings);

        // Preview-first selection: flows the source deliberately excludes are not projected as pipelines (and, being
        // absent from the present set, are deactivated below if a previous sync had imported them, keeping their
        // history). The selection is the ONLY gate; everything else stays the same one sync path.
        var flows = excludedFlowPaths is { Count: > 0 }
            ? collected.Flows.Where(f => !excludedFlowPaths.Contains(f.File)).ToList()
            : collected.Flows;

        var (pipelines, presentIds, schedules) = PreparePipelines(root, repoId, flows, collected.Schedules, nowUtc, warnings, ct);

        // The known run ids, read outside the transaction, so only new artifacts are parsed and retained.
        var knownRunIds = (await context.Runs.Where(r => r.RepoId == repoId).Select(r => r.RunId)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var (runs, runsSkipped, runsFailed) = await PrepareRunsAsync(root, repoId, knownRunIds, warnings, ct).ConfigureAwait(false);
        try
        {
            // ---- Phase two: one serializable transaction for the reconciliation writes only (so concurrent
            // syncs of the same repo serialize instead of racing), run through the context's execution strategy
            // so it is a single retriable unit. The control plane enables connection resiliency
            // (EnableRetryOnFailure); EF then forbids a user-initiated transaction unless wrapped this way. A
            // retried attempt re-stages its rows from the phase-one artifacts, never from half-tracked state.
            return await CatalogTransaction.InSerializableAsync(context, async () =>
            {
                await UpsertRepoAsync(context, repoId, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
                var pipelineTally = await ApplyPipelinesAsync(context, repoId, nowUtc, pipelines, presentIds, schedules, excludedFlowPaths, ct).ConfigureAwait(false);
                var runTally = await ApplyRunsAsync(context, runs, runsSkipped, runsFailed, ct).ConfigureAwait(false);

                // The kinds' own document families (mappings, snapshots) reconcile in the same transaction, so a
                // sync is one atomic view of the repository.
                var documentTally = CatalogSyncExtensionResult.Empty;
                foreach (var extension in _extensions)
                {
                    documentTally = documentTally.Add(await extension.SyncAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false));
                }

                return new CatalogSyncResult
                {
                    DocumentsAdded = documentTally.Added,
                    DocumentsUpdated = documentTally.Updated,
                    DocumentsUnchanged = documentTally.Unchanged,
                    DocumentsRemoved = documentTally.Removed,
                    DocumentsInvalid = documentTally.Invalid,
                    PipelinesAdded = pipelineTally.Added,
                    PipelinesUpdated = pipelineTally.Updated,
                    PipelinesUnchanged = pipelineTally.Unchanged,
                    PipelinesDeactivated = pipelineTally.Deactivated,
                    PipelinesDeleted = pipelineTally.Deleted,
                    RunsAdded = runTally.Added,
                    RunsSkipped = runTally.Skipped,
                    RunsFailed = runTally.Failed,
                    RunEventsAdded = runTally.Events,
                    Warnings = warnings,
                };
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }
    }

    private static async Task UpsertRepoAsync(
        CatalogDbContext context, Guid repoId, string repoName, string? remoteUrl, string root, DateTime nowUtc, CancellationToken ct)
    {
        var repo = await context.Repos.FindAsync([repoId], ct).ConfigureAwait(false);
        if (repo is null)
        {
            context.Repos.Add(new CatalogRepo
            {
                Id = repoId,
                Name = repoName,
                RemoteUrl = NullIfBlank(remoteUrl),
                RootPath = root,
                FirstSeenUtc = nowUtc,
                LastSyncUtc = nowUtc,
            });
        }
        else
        {
            repo.Name = repoName;
            repo.RemoteUrl = NullIfBlank(remoteUrl) ?? repo.RemoteUrl;
            repo.RootPath = root;
            repo.LastSyncUtc = nowUtc;
        }
    }

    /// <summary>
    /// Phase-one preparation of this repo's pipeline projections: reads and parses each present flow document
    /// exactly once, producing everything the reconciliation transaction stages (the redacted YAML, its hash,
    /// the definition JSON) plus the validated schedule mirror entries. File IO and pure computation only;
    /// nothing here touches the database.
    /// </summary>
    private (List<PreparedPipeline> Pipelines, HashSet<Guid> PresentIds, List<PreparedSchedule> Schedules) PreparePipelines(
        string root, Guid repoId, IReadOnlyList<CollectedFlow> flows, IReadOnlyList<CollectedSchedule> collectedSchedules,
        DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var pipelines = new List<PreparedPipeline>();
        var present = new HashSet<Guid>();

        foreach (var flow in flows)
        {
            ct.ThrowIfCancellationRequested();
            var id = CatalogIdentity.Pipeline(repoId, flow.Name);
            if (!present.Add(id))
            {
                continue; // a duplicate flow name (already warned by the estate scan); the first wins.
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, flow.File));
            var rawYaml = ReadYaml(fullPath, flow.File, warnings);
            if (rawYaml is null)
            {
                // The file vanished or was unreadable since the estate scan: leave any existing row untouched (it
                // stays in 'present' so it is not deactivated) rather than overwriting it with empty content.
                continue;
            }

            if (SecretHygiene.LooksLikeEmbeddedSecret(rawYaml))
            {
                warnings.Add($"'{flow.Name}' ({flow.File}) appears to embed a credential; it is redacted in the catalog, but secrets must be ${{env:...}}/${{keyvault:...}} references in the YAML, not literals.");
            }

            // Redact before anything is stored or hashed, so no credential ever rests in the catalog and change
            // detection runs on the safe form. Clean reference-only YAML passes through unchanged.
            var yaml = SecretHygiene.RedactedMessage(rawYaml);
            var hash = CatalogProjection.Hash(yaml);

            // The queryable definition JSON. A document that fails to parse still lands as a pipeline row (the YAML
            // text is the source of truth), just without it; that is never a reason to fail the sync.
            var definitionJson = string.Empty;
            try
            {
                var document = _documents.Parse(rawYaml, fullPath);
                definitionJson = SerializeDefinition(document, fullPath, warnings);
            }
            catch (SqlFlowException ex)
            {
                // FlowValidationException derives from SqlFlowException, so a malformed document is caught here too.
                warnings.Add($"'{fullPath}' could not be parsed for the catalog definition ({SecretHygiene.RedactedMessage(ex)}); stored without it.");
            }

            pipelines.Add(new PreparedPipeline
            {
                Flow = flow,
                Id = id,
                Yaml = yaml,
                ContentHash = hash,
                DefinitionJson = definitionJson,
            });
        }

        // Mirror git-declared schedules. A schedule is defined once by NAME (a schedules.yaml entry, or an inline
        // block on a flow) and flows JOIN it by name; the estate scan has already resolved both shapes, and every
        // reference, into a named schedule with its member set. Validated here (pure); the staging into the schedule
        // and member tables happens inside the sync's transaction.
        var presentNames = flows.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schedules = new List<PreparedSchedule>();
        foreach (var schedule in collectedSchedules)
        {
            var spec = schedule.Spec;
            // A CHAINED schedule is driven by its parent's completion, not by the clock, so it carries neither a cron
            // nor an interval. Only clock-driven schedules are validated here.
            if (!spec.IsChained
                && !ScheduleClock.TryValidate(spec.Cron, spec.IntervalSeconds, spec.Timezone, out var scheduleError))
            {
                warnings.Add($"schedule '{schedule.Name}' ({schedule.Origin}) is invalid: {scheduleError}");
                continue;
            }

            // A member the selection excluded from this sync has no pipeline row to enqueue, so it is not stored as a
            // member. Leaving it out keeps the stored member set an honest answer to "what does this fire run".
            var members = schedule.Members.Where(presentNames.Contains).ToList();
            var nextFire = ScheduleClock.NextFire(spec.Cron, spec.IntervalSeconds, spec.Timezone, nowUtc) ?? nowUtc;
            schedules.Add(new PreparedSchedule(
                schedule.Name, members, spec, nextFire,
                new ScheduleDefinitionSource(schedule.OriginFile, schedule.OriginFlow, schedule.LibraryYaml)));
        }

        return (pipelines, present, schedules);
    }

    /// <summary>Reconciles this repo's pipeline rows and git-declared schedules from the phase-one preparation.
    /// Runs inside the sync's transaction and performs only database work.</summary>
    private static async Task<(int Added, int Updated, int Unchanged, int Deactivated, int Deleted)> ApplyPipelinesAsync(
        CatalogDbContext context, Guid repoId, DateTime nowUtc,
        IReadOnlyList<PreparedPipeline> pipelines, IReadOnlySet<Guid> presentIds,
        IReadOnlyList<PreparedSchedule> schedules, IReadOnlySet<string>? excludedFlowPaths, CancellationToken ct)
    {
        // Only this repo's pipelines: another repo's flows in the same catalog must not be touched by this sync.
        // AsTracking so the update/deactivate mutations below persist even when the host's context defaults to
        // NoTracking (the control plane pools its context that way); on a tracking context this is a no-op.
        var existing = await context.Pipelines.Where(p => p.RepoId == repoId).AsTracking().ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var prepared in pipelines)
        {
            ct.ThrowIfCancellationRequested();
            if (existing.TryGetValue(prepared.Id, out var row) && string.Equals(row.ContentHash, prepared.ContentHash, StringComparison.Ordinal))
            {
                // Unchanged content (derived fields cannot have changed either): re-affirm presence only.
                row.Active = true;
                row.LastSeenUtc = nowUtc;
                row.RelativePath = prepared.Flow.File;
                unchanged++;
                continue;
            }

            var flow = prepared.Flow;
            var projected = CatalogProjection.Pipeline(
                repoId, flow.Name, flow.Kind, flow.Batch, flow.File,
                flow.SourceReference, flow.TargetReference, prepared.ContentHash, prepared.Yaml, prepared.DefinitionJson, nowUtc,
                flow.Mode, flow.Lifecycle);

            if (row is not null)
            {
                CopyProjection(row, projected, nowUtc);
                updated++;
            }
            else
            {
                context.Pipelines.Add(projected);
                added++;
            }
        }

        // A flow excluded from a selection-scoped sync is still in the repo, so it is deactivated and reactivates
        // when it is re-included. A flow that has genuinely left the repo is deleted, so it stops appearing in the
        // catalog instead of piling up as an inactive tombstone across renames. Its run history is KEPT (each run
        // carries the flow name, so the traces stand on their own), but its schedule memberships are removed so
        // nothing fires for a flow that is gone.
        var deactivated = 0;
        var removedIds = new List<Guid>();
        foreach (var (id, row) in existing)
        {
            if (presentIds.Contains(id))
            {
                continue;
            }

            if (excludedFlowPaths is { Count: > 0 } && excludedFlowPaths.Contains(row.RelativePath))
            {
                if (row.Active)
                {
                    row.Active = false;
                    deactivated++;
                }
            }
            else
            {
                removedIds.Add(id);
            }
        }

        var deleted = removedIds.Count;
        if (deleted > 0)
        {
            await context.ScheduleMembers.Where(m => removedIds.Contains(m.PipelineId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            context.Pipelines.RemoveRange(removedIds.Select(id => existing[id]));
        }

        // Stage the validated yaml schedule mirror, each schedule with the member set git says joined it: an
        // operator's API pause is preserved and API-created schedules are never touched; a schedule that left git is
        // removed with its memberships. Staged on this context so the changes commit inside the sync's own
        // transaction (the store's transaction-free variants).
        var scheduleKeep = new HashSet<Guid>();
        foreach (var schedule in schedules)
        {
            var scheduleId = await ScheduleStore.StageYamlUpsertAsync(
                context, repoId, schedule.Name, schedule.Members, schedule.Spec.Cron, schedule.Spec.IntervalSeconds,
                schedule.Spec.Timezone, schedule.Spec.Enabled, schedule.Spec.Catchup, schedule.Spec.MaxConcurrency,
                schedule.NextFireUtc, nowUtc, schedule.Definition, schedule.Spec.After,
                schedule.Spec.ParentFreshnessHours, ct).ConfigureAwait(false);
            scheduleKeep.Add(scheduleId);
        }

        await ScheduleStore.StageRemoveYamlSchedulesNotInAsync(context, repoId, scheduleKeep, ct).ConfigureAwait(false);

        return (added, updated, unchanged, deactivated, deleted);
    }

    private static void CopyProjection(CatalogPipeline row, CatalogPipeline projected, DateTime nowUtc)
    {
        row.Name = projected.Name;
        row.Kind = projected.Kind;
        row.Batch = projected.Batch;
        row.RelativePath = projected.RelativePath;
        row.ExecutionMode = projected.ExecutionMode;
        row.Lifecycle = projected.Lifecycle;
        row.SourceServer = projected.SourceServer;
        row.TargetServer = projected.TargetServer;
        row.ContentHash = projected.ContentHash;
        row.Yaml = projected.Yaml;
        row.DefinitionJson = projected.DefinitionJson;
        row.Active = true;
        row.LastSeenUtc = nowUtc;
    }

    /// <summary>Phase-one scan of the estate's run artifacts: parses each <c>run.json</c> once and keeps ONLY the
    /// runs the catalog does not already know, so nothing is read or parsed twice per pass. Known and duplicate
    /// runs count as skipped; unreadable, malformed, or over-limit artifacts as failed. The caller owns disposing
    /// the returned documents once the write phase is done with them.</summary>
    private static async Task<(List<PreparedRun> Runs, int Skipped, int Failed)> PrepareRunsAsync(
        string root, Guid repoId, HashSet<Guid> knownRunIds, List<string> warnings, CancellationToken ct)
    {
        var prepared = new List<PreparedRun>();
        var seen = new HashSet<Guid>();
        var skipped = 0;
        var failed = 0;

        foreach (var file in EnumerateRunArtifacts(root))
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument? document = null;
            try
            {
                var length = new FileInfo(file).Length;
                if (length > MaxRunJsonBytes)
                {
                    warnings.Add($"run artifact '{file}' is {length} bytes, over the {MaxRunJsonBytes}-byte limit; skipped.");
                    failed++;
                    continue;
                }

                document = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                var run = CatalogProjection.RunFromJson(document.RootElement, repoId);
                if (run is null)
                {
                    warnings.Add($"run artifact '{file}' is missing required fields; skipped.");
                    failed++;
                    continue;
                }

                if (knownRunIds.Contains(run.RunId) || !seen.Add(run.RunId))
                {
                    skipped++; // immutable and already recorded (or seen earlier this pass).
                    continue;
                }

                prepared.Add(new PreparedRun(run, document));
                document = null; // ownership handed to the prepared list.
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"run artifact '{file}' could not be read ({SecretHygiene.RedactedMessage(ex)}); skipped.");
                failed++;
            }
            finally
            {
                document?.Dispose();
            }
        }

        return (prepared, skipped, failed);
    }

    /// <summary>Inserts the phase-one runs and their event timelines. Runs inside the sync's transaction.</summary>
    private static async Task<RunSyncTally> ApplyRunsAsync(
        CatalogDbContext context, IReadOnlyList<PreparedRun> runs, int skippedInScan, int failedInScan, CancellationToken ct)
    {
        var skipped = skippedInScan;
        var added = 0;
        var events = 0;

        // The pre-transaction scan already dropped every run the catalog knew then; re-verify the survivors
        // INSIDE the transaction (in bounded chunks) so a run another node recorded in the meantime is skipped,
        // keeping the insert idempotent under concurrency.
        var alreadyKnown = (await SelectByKeysAsync(
                runs.Select(r => r.Run.RunId).ToList(),
                chunk => context.Runs.Where(r => chunk.Contains(r.RunId)).Select(r => r.RunId).ToListAsync(ct))
            .ConfigureAwait(false)).ToHashSet();

        foreach (var prepared in runs)
        {
            ct.ThrowIfCancellationRequested();
            if (alreadyKnown.Contains(prepared.Run.RunId))
            {
                skipped++; // recorded by a concurrent sync/write-back since the scan; a run is immutable.
                continue;
            }

            // A run is immutable, so its event timeline is inserted exactly once, with the run itself.
            context.Runs.Add(prepared.Run);
            added++;
            events += AddRunDetail(context, prepared.Document.RootElement, prepared.Run.RunId, prepared.Run.RepoId ?? Guid.Empty);
        }

        return new RunSyncTally(added, skipped, failedInScan, events);
    }

    /// <summary>Adds the immutable event timeline of one run projected from its run.json root. Shared by the full
    /// estate sync and the per-run write-back, so the projection is wired in exactly one place.</summary>
    /// <param name="context">The catalog context the rows are added to.</param>
    /// <param name="root">The run.json root element the events are projected from.</param>
    /// <param name="runId">The run the events belong to.</param>
    /// <param name="repoId">The repo the run belongs to.</param>
    /// <param name="existingMaxEventOrdinal">The highest event ordinal already present as a live-streamed row (the
    /// node writes canonical events into the catalog as the run executes). Events at or below it are skipped so
    /// only the missing tail is appended, leaving the live rows - and the stable ids the trace stream already
    /// delivered - untouched. 0 for the CLI and full-sync paths, which have no live rows, so the whole event
    /// timeline is inserted.</param>
    /// <returns>How many event rows were added.</returns>
    internal static int AddRunDetail(CatalogDbContext context, JsonElement root, Guid runId, Guid repoId, int existingMaxEventOrdinal = 0)
    {
        var events = 0;
        foreach (var runEvent in CatalogProjection.RunEvents(root, runId, repoId))
        {
            if (runEvent.Ordinal <= existingMaxEventOrdinal)
            {
                continue; // already written live under a stable id; do not re-issue it
            }

            context.RunEvents.Add(runEvent);
            events++;
        }

        return events;
    }

    /// <summary>
    /// The self-maintaining write-back: records ONE just-completed run (and ensures its pipeline row) into the
    /// catalog, so a configured database stays current without a manual full <see cref="SyncAsync"/>. It upserts
    /// the repo and the single flow that produced the run, then inserts the run and its events (idempotent: a run
    /// already recorded is left untouched, since a run is immutable). It deliberately does NOT scan sibling flows
    /// or mirror schedules (membership is a repo-wide fact only the full sync can establish), so the per-run cost
    /// is bounded. Runs in one serializable transaction; the caller treats any failure as non-fatal to the run.
    /// </summary>
    public async Task<RecordRunResult> RecordRunAsync(
        CatalogDbContext context, string flowFilePath, string runJsonPath, string repoName, string? repoRemoteUrl,
        DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);

        var fullFlowPath = Path.GetFullPath(flowFilePath);
        var root = Path.GetDirectoryName(fullFlowPath)
            ?? throw new SqlFlowException($"'{flowFilePath}' has no parent directory.");
        var repoId = FlowIdentity.FromName(repoName);
        return await CatalogTransaction.InSerializableAsync(context, async () =>
        {
            var warnings = new List<string>();
            await UpsertRepoAsync(context, repoId, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
            var pipelineChange = await UpsertSinglePipelineAsync(context, root, fullFlowPath, repoId, nowUtc, warnings, ct).ConfigureAwait(false);

            var runRecorded = false;
            var events = 0;

            // A corrupt, oversized, or unreadable run.json is reported as a warning and the run is simply not
            // recorded; the pipeline upsert above still commits (consistent with how the full sync treats a bad
            // artifact, and with the method's non-fatal contract).
            try
            {
                var length = new FileInfo(runJsonPath).Length;
                if (length > MaxRunJsonBytes)
                {
                    warnings.Add($"run artifact '{runJsonPath}' is {length} bytes, over the {MaxRunJsonBytes}-byte limit; not recorded.");
                }
                else
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(runJsonPath, ct).ConfigureAwait(false));
                    var run = CatalogProjection.RunFromJson(document.RootElement, repoId);
                    if (run is null)
                    {
                        warnings.Add($"run artifact '{runJsonPath}' is missing required fields; not recorded.");
                    }
                    else if (await context.Runs.FindAsync([run.RunId], ct).ConfigureAwait(false) is null)
                    {
                        context.Runs.Add(run);
                        runRecorded = true;
                        events = AddRunDetail(context, document.RootElement, run.RunId, repoId);
                    }

                    // else: already recorded (a prior full sync or write-back); a run is immutable, so nothing to do.
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"run artifact '{runJsonPath}' could not be read ({SecretHygiene.RedactedMessage(ex)}); not recorded.");
            }

            return new RecordRunResult
            {
                PipelineChange = pipelineChange,
                RunRecorded = runRecorded,
                RunEventsAdded = events,
                Warnings = warnings,
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Upserts the single flow that produced a run by loading and parsing JUST that document (a targeted
    /// read, never an estate scan), reusing the same redaction, hashing, and projection as the full pipeline
    /// sync. An unchanged flow only re-affirms its presence. A missing, unreadable, or unparseable document is a
    /// warning and the run is recorded without a pipeline row.</summary>
    private async Task<PipelineChange> UpsertSinglePipelineAsync(
        CatalogDbContext context, string root, string fullFlowPath, Guid repoId, DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var relativePath = Normalize(Path.GetRelativePath(root, fullFlowPath));
        var rawYaml = ReadYaml(fullFlowPath, relativePath, warnings);
        if (rawYaml is null)
        {
            return PipelineChange.None;
        }

        FlowDocument document;
        try
        {
            document = _documents.Parse(rawYaml, fullFlowPath);
        }
        catch (SqlFlowException ex)
        {
            warnings.Add($"'{fullFlowPath}' could not be parsed as a flow ({SecretHygiene.RedactedMessage(ex)}); its run is recorded without a pipeline row.");
            return PipelineChange.None;
        }

        if (SecretHygiene.LooksLikeEmbeddedSecret(rawYaml))
        {
            warnings.Add($"'{document.Name}' ({relativePath}) appears to embed a credential; it is redacted in the catalog, but secrets must be ${{env:...}}/${{keyvault:...}} references in the YAML, not literals.");
        }

        var yaml = SecretHygiene.RedactedMessage(rawYaml);
        var hash = CatalogProjection.Hash(yaml);

        var id = CatalogIdentity.Pipeline(repoId, document.Name);
        var row = await context.Pipelines.FindAsync([id], ct).ConfigureAwait(false);
        if (row is not null && string.Equals(row.ContentHash, hash, StringComparison.Ordinal))
        {
            row.Active = true;
            row.LastSeenUtc = nowUtc;
            row.RelativePath = relativePath;
            return PipelineChange.Unchanged;
        }

        var definitionJson = SerializeDefinition(document, fullFlowPath, warnings);
        var projected = CatalogProjection.Pipeline(
            repoId, document.Name, document.Kind, document.Batch, relativePath,
            document.SourceReference, document.TargetReference, hash, yaml, definitionJson, nowUtc,
            document.Mode, document.Lifecycle);

        if (row is not null)
        {
            CopyProjection(row, projected, nowUtc);
            return PipelineChange.Updated;
        }

        context.Pipelines.Add(projected);
        return PipelineChange.Added;
    }

    /// <summary>Runs a keyed query per <see cref="KeyChunkSize"/> chunk and concatenates the rows.</summary>
    private static async Task<List<TRow>> SelectByKeysAsync<TKey, TRow>(
        IReadOnlyList<TKey> keys, Func<TKey[], Task<List<TRow>>> query)
    {
        var rows = new List<TRow>();
        foreach (var chunk in keys.Chunk(KeyChunkSize))
        {
            rows.AddRange(await query(chunk).ConfigureAwait(false));
        }

        return rows;
    }

    /// <summary>Serializes an already-parsed flow document into the queryable, secret-redacted definition JSON.
    /// A serialization failure degrades to an empty definition with a warning (the definition is an enrichment,
    /// never a reason to fail the sync).</summary>
    private static string SerializeDefinition(FlowDocument document, string fullPath, List<string> warnings)
    {
        try
        {
            var json = JsonSerializer.Serialize(document, document.GetType(), DefinitionJsonOptions);
            return SecretHygiene.RedactedMessage(json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            warnings.Add($"'{fullPath}' could not be serialized for the catalog definition ({SecretHygiene.RedactedMessage(ex)}); stored without it.");
            return string.Empty;
        }
    }

    /// <summary>Reads a flow document's text, or null when it is gone, too large, or unreadable (the caller skips
    /// it without overwriting any existing row). A removed-since-scan file is reported clearly.</summary>
    private static string? ReadYaml(string fullPath, string relativePath, List<string> warnings)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                warnings.Add($"'{relativePath}' was not found when reading its text (removed during the sync?); left unchanged.");
                return null;
            }

            var length = new FileInfo(fullPath).Length;
            if (length > MaxYamlBytes)
            {
                warnings.Add($"'{relativePath}' is {length} bytes, over the {MaxYamlBytes}-byte limit; skipped.");
                return null;
            }

            return File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"'{relativePath}' could not be read ({SecretHygiene.RedactedMessage(ex)}); left unchanged.");
            return null;
        }
    }

    /// <summary>Every <c>run.json</c> under a <c>.sqlflow/runs</c> tree in the estate.</summary>
    private static IEnumerable<string> EnumerateRunArtifacts(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "run.json", SearchOption.AllDirectories)
            .Where(f => f.Replace('\\', '/').Contains("/.sqlflow/runs/", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
