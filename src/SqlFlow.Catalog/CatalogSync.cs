using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;

namespace SqlFlow.Catalog;

/// <summary>The tally of one sync pass.</summary>
public sealed record CatalogSyncResult
{
    public int PipelinesAdded { get; init; }
    public int PipelinesUpdated { get; init; }
    public int PipelinesUnchanged { get; init; }
    public int PipelinesDeactivated { get; init; }
    public int RunsAdded { get; init; }
    public int RunsSkipped { get; init; }
    public int RunsFailed { get; init; }
    public int ObjectsUpserted { get; init; }

    /// <summary>Database-less twin rows deleted because their object now syncs under a database-qualified
    /// key and no repo's edges reference the weak key anymore (identity healing).</summary>
    public int ObjectsSuperseded { get; init; }

    public int ObjectColumns { get; init; }
    public int LineageEdges { get; init; }
    public int FlowDependencies { get; init; }
    public int Waves { get; init; }
    public int RunFilesAdded { get; init; }
    public int RunAssertionsAdded { get; init; }
    public int RunStatementsAdded { get; init; }
    public int RunSurrogateKeysAdded { get; init; }
    public int RunHealthCheckMetricsAdded { get; init; }
    public bool LineageConnected { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The per-pass tally of run artifacts and the drill-down detail rows projected from them.</summary>
internal readonly record struct RunSyncTally(
    int Added, int Skipped, int Failed, int Files, int Assertions, int Statements, int SurrogateKeys, int Metrics);

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
    public int RunFilesAdded { get; init; }
    public int RunAssertionsAdded { get; init; }
    public int RunStatementsAdded { get; init; }
    public int RunSurrogateKeysAdded { get; init; }
    public int RunHealthCheckMetricsAdded { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Synchronizes the shadow catalog FROM the source of truth: the git/YAML estate (the pipeline registry, mapped
/// into rows with the full definition in queryable JSON) and the on-disk <c>run.json</c> history. One direction
/// only - the database mirrors the files, never the reverse - so the catalog can always be rebuilt by re-syncing,
/// and several repos sync into one catalog for cross-repo queries. Pipelines are upserted by their stable
/// identity (ones that have left the estate are deactivated, keeping their run history); runs are inserted by
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
    // to one parameter per key, so membership queries and deletes over report-sized key sets run in bounded
    // chunks whose results/effects are combined. 500 keys per round trip stays far under the limit.
    private const int KeyChunkSize = 500;

    private static readonly JsonSerializerOptions DefinitionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FlowSetCollector _estate = new();

    private readonly YamlDocumentLoader _documents = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader());

    /// <summary>One estate flow prepared for the reconciliation transaction: its redacted text, content hash,
    /// serialized definition, and the parsed document (null when it failed to parse after the scan) that the
    /// declared-column projection reads. Prepared once per pass; the transaction (and any retry of it) stages
    /// fresh rows from this data.</summary>
    private sealed record PreparedPipeline
    {
        public required CollectedFlow Flow { get; init; }

        public required Guid Id { get; init; }

        public required string Yaml { get; init; }

        public required string ContentHash { get; init; }

        public required string DefinitionJson { get; init; }

        public required FlowDocument? Document { get; init; }
    }

    /// <summary>One validated git-declared schedule, ready to stage into the schedule table.</summary>
    private sealed record PreparedSchedule(string FlowName, Core.ScheduleSpec Spec, DateTime NextFireUtc);

    /// <summary>One run artifact awaiting insertion: the projected header row (client-keyed, so re-adding it on
    /// a transaction retry is safe) and the parsed document its detail rows project from inside the transaction,
    /// retained so the file is read and parsed exactly once per pass.</summary>
    private sealed record PreparedRun(CatalogRun Run, JsonDocument Document) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    public async Task<CatalogSyncResult> SyncAsync(
        CatalogDbContext context, string estateDirectory, string repoName, string? repoRemoteUrl, DateTime nowUtc,
        bool includeDerived = false, ISecretResolver? secrets = null, IReadOnlySet<string>? excludedFlowPaths = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(estateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        var root = Path.GetFullPath(estateDirectory);
        var repoId = FlowIdentity.FromName(repoName);
        var warnings = new List<string>();

        // ---- Phase one: pure computation and reads, before any transaction. The estate is collected once and
        // each present document read and parsed once; the parsed artifacts (the collected flow set, the loaded
        // documents, the redacted text) flow into every consumer below, including the lineage computation.
        var collected = _estate.Collect(root);
        warnings.AddRange(collected.Warnings);

        // Preview-first selection: flows the source deliberately excludes are not projected as pipelines (and, being
        // absent from the present set, are deactivated below if a previous sync had imported them, keeping their
        // history). The selection is the ONLY gate; everything else stays the same one sync path.
        var flows = excludedFlowPaths is { Count: > 0 }
            ? collected.Flows.Where(f => !excludedFlowPaths.Contains(Normalize(f.Node.File))).ToList()
            : collected.Flows;

        var (pipelines, presentIds, schedules, anyUnreadable) = PreparePipelines(root, repoId, flows, nowUtc, warnings, ct);

        // The stored state this pass reconciles against, read outside the transaction: the known run ids (so only
        // new artifacts are parsed and retained) and the active pipelines' content hashes (the lineage gate).
        var knownRunIds = (await context.Runs.Where(r => r.RepoId == repoId).Select(r => r.RunId)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var storedActiveHashes = await context.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active)
            .Select(p => new { p.Id, p.ContentHash })
            .ToDictionaryAsync(p => p.Id, p => p.ContentHash, ct).ConfigureAwait(false);

        var (runs, runsSkipped, runsFailed) = await PrepareRunsAsync(root, repoId, knownRunIds, warnings, ct).ConfigureAwait(false);
        try
        {
            // The lineage recompute gate: recompute only when an input of the graph could have changed. The
            // declared tier's input is the flow set (content hashes, additions, removals); the observed tier's
            // input is the run history (any newly discovered artifact); the derived tier reads the live
            // catalogs, which can change on their own, so a connected sync always recomputes. An excluded flow
            // still contributes lineage (the graph spans the whole estate) but has no stored hash to compare,
            // so a selection-scoped sync recomputes too. When nothing changed, the stored lineage IS current.
            var anyExcluded = collected.Flows.Count != flows.Count;
            var lineageNeeded = includeDerived || anyExcluded || runs.Count > 0
                || LineageInputsChanged(pipelines, anyUnreadable, storedActiveHashes);

            LineageReport? report = null;
            string? lineageFailure = null;
            if (lineageNeeded)
            {
                try
                {
                    // Reuses the flow set collected above, so the estate is never scanned or parsed a second time.
                    report = await LineageService.ComputeAsync(
                        new LineageOptions { FlowDirectory = root, IncludeObserved = true, IncludeDerived = includeDerived, Secrets = secrets },
                        collected, ct).ConfigureAwait(false);
                    foreach (var warning in report.Warnings)
                    {
                        warnings.Add($"lineage: {warning}");
                    }
                }
                catch (Exception ex) when (ex is SqlFlow.Core.SqlFlowException or IOException or InvalidOperationException)
                {
                    // Lineage is an enrichment: a failure to compute it (e.g. a connect-tier timeout) must not
                    // fail the whole sync. The pipeline registry and run history still land; the write phase
                    // resets the stale waves so they never read as a real execution order.
                    lineageFailure = SecretHygiene.RedactedMessage(ex.Message);
                    warnings.Add($"lineage was not computed for this sync ({lineageFailure}); objects and edges left unchanged, waves reset to not-computed.");
                }
            }

            // ---- Phase two: one serializable transaction for the reconciliation writes only (so concurrent
            // syncs of the same repo serialize instead of racing), run through the context's execution strategy
            // so it is a single retriable unit. The control plane enables connection resiliency
            // (EnableRetryOnFailure); EF then forbids a user-initiated transaction unless wrapped this way. A
            // retried attempt re-stages its rows from the phase-one artifacts, never from half-tracked state.
            return await CatalogTransaction.InSerializableAsync(context, async () =>
            {
                await UpsertRepoAsync(context, repoId, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
                var pipelineTally = await ApplyPipelinesAsync(context, repoId, nowUtc, pipelines, presentIds, schedules, ct).ConfigureAwait(false);
                var runTally = await ApplyRunsAsync(context, repoId, runs, runsSkipped, runsFailed, ct).ConfigureAwait(false);

                (int Objects, int Superseded, int Columns, int Edges, int FlowDeps, int Waves, bool Connected) lineage;
                if (report is not null)
                {
                    lineage = await ApplyLineageAsync(context, repoId, report, includeDerived, nowUtc, ct).ConfigureAwait(false);
                }
                else
                {
                    if (lineageFailure is not null)
                    {
                        // A wave from an earlier successful sync no longer provably reflects this estate, so reset
                        // every active pipeline in this repo to the -1 "not computed" sentinel rather than leaving
                        // a stale wave that reads as a real execution order. The pipelines are already tracked
                        // (loaded in ApplyPipelinesAsync), so mutating them here is persisted by the single
                        // SaveChangesAsync at the end of the pass.
                        foreach (var pipeline in context.Pipelines.Local)
                        {
                            if (pipeline.RepoId == repoId && pipeline.Active)
                            {
                                pipeline.Wave = -1;
                            }
                        }
                    }

                    // else: no lineage input changed since the stored state (same content hashes, no additions or
                    // removals, no new runs, offline): the stored objects, edges, waves, and dependencies are
                    // already current, so the recompute is skipped and nothing lineage-related is written.
                    lineage = (0, 0, 0, 0, 0, 0, false);
                }

                return new CatalogSyncResult
                {
                    PipelinesAdded = pipelineTally.Added,
                    PipelinesUpdated = pipelineTally.Updated,
                    PipelinesUnchanged = pipelineTally.Unchanged,
                    PipelinesDeactivated = pipelineTally.Deactivated,
                    RunsAdded = runTally.Added,
                    RunsSkipped = runTally.Skipped,
                    RunsFailed = runTally.Failed,
                    RunFilesAdded = runTally.Files,
                    RunAssertionsAdded = runTally.Assertions,
                    RunStatementsAdded = runTally.Statements,
                    RunSurrogateKeysAdded = runTally.SurrogateKeys,
                    RunHealthCheckMetricsAdded = runTally.Metrics,
                    ObjectsUpserted = lineage.Objects,
                    ObjectsSuperseded = lineage.Superseded,
                    ObjectColumns = lineage.Columns,
                    LineageEdges = lineage.Edges,
                    FlowDependencies = lineage.FlowDeps,
                    Waves = lineage.Waves,
                    LineageConnected = lineage.Connected,
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

    // The retriable serializable-transaction wrapper lives in CatalogTransaction so the run-queue lifecycle shares
    // the exact same execution-strategy + change-tracker-reset semantics as the sync/write-back.

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
    /// the definition JSON, the parsed document for the declared-column projection) plus the validated schedule
    /// mirror entries. File IO and pure computation only; nothing here touches the database.
    /// </summary>
    private (List<PreparedPipeline> Pipelines, HashSet<Guid> PresentIds, List<PreparedSchedule> Schedules, bool AnyUnreadable) PreparePipelines(
        string root, Guid repoId, IReadOnlyList<CollectedFlow> flows, DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var pipelines = new List<PreparedPipeline>();
        var present = new HashSet<Guid>();
        var anyUnreadable = false;

        foreach (var flow in flows)
        {
            ct.ThrowIfCancellationRequested();
            var id = CatalogIdentity.Pipeline(repoId, flow.Node.Name);
            if (!present.Add(id))
            {
                continue; // a duplicate flow name (already warned by the estate scan); the first wins.
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, flow.Node.File));
            var rawYaml = ReadYaml(fullPath, flow.Node.File, warnings);
            if (rawYaml is null)
            {
                // The file vanished or was unreadable since the estate scan: leave any existing row untouched (it
                // stays in 'present' so it is not deactivated) rather than overwriting it with empty content.
                anyUnreadable = true;
                continue;
            }

            if (SecretHygiene.LooksLikeEmbeddedSecret(rawYaml))
            {
                warnings.Add($"'{flow.Node.Name}' ({flow.Node.File}) appears to embed a credential; it is redacted in the catalog, but secrets must be ${{env:...}}/${{keyvault:...}} references in the YAML, not literals.");
            }

            // Redact before anything is stored or hashed, so no credential ever rests in the catalog and change
            // detection runs on the safe form. Clean reference-only YAML passes through unchanged.
            var yaml = SecretHygiene.RedactedMessage(rawYaml);
            var hash = CatalogProjection.Hash(yaml);

            // ONE parse serves both the queryable definition JSON and the declared-column projection. A document
            // that fails to parse still lands as a pipeline row (the YAML text is the source of truth), just
            // without those enrichments; that is never a reason to fail the sync.
            FlowDocument? document = null;
            var definitionJson = string.Empty;
            try
            {
                document = _documents.Parse(rawYaml, fullPath);
                definitionJson = SerializeDefinition(document, fullPath, warnings);
            }
            catch (SqlFlow.Core.SqlFlowException ex)
            {
                // FlowValidationException derives from SqlFlowException, so a malformed document is caught here too.
                warnings.Add($"'{fullPath}' could not be parsed for the catalog definition ({SecretHygiene.RedactedMessage(ex.Message)}); stored without it.");
            }

            pipelines.Add(new PreparedPipeline
            {
                Flow = flow,
                Id = id,
                Yaml = yaml,
                ContentHash = hash,
                DefinitionJson = definitionJson,
                Document = document,
            });
        }

        // Mirror git-declared schedules: a flow's schedule lives in its YAML and is validated here (pure); the
        // staging into the schedule table happens inside the sync's transaction.
        var schedules = new List<PreparedSchedule>();
        var scheduledPipelineIds = new HashSet<Guid>();
        foreach (var flow in flows)
        {
            var pipelineId = CatalogIdentity.Pipeline(repoId, flow.Node.Name);
            if (!scheduledPipelineIds.Add(pipelineId) || flow.Schedule is not { } spec)
            {
                continue; // a duplicate flow name (first wins) or no schedule declared
            }

            if (!ScheduleClock.TryValidate(spec.Cron, spec.IntervalSeconds, spec.Timezone, out var scheduleError))
            {
                warnings.Add($"'{flow.Node.Name}' ({flow.Node.File}) has an invalid schedule: {scheduleError}");
                continue;
            }

            var nextFire = ScheduleClock.NextFire(spec.Cron, spec.IntervalSeconds, spec.Timezone, nowUtc) ?? nowUtc;
            schedules.Add(new PreparedSchedule(flow.Node.Name, spec, nextFire));
        }

        return (pipelines, present, schedules, anyUnreadable);
    }

    /// <summary>Whether the declared tier's lineage inputs differ from what the stored catalog reflects: any
    /// pipeline added, removed, or with changed content since the stored state. Doubt (a file unreadable during
    /// this pass, a stored hash that is blank) counts as changed, so the caller recomputes.</summary>
    private static bool LineageInputsChanged(
        IReadOnlyList<PreparedPipeline> pipelines, bool anyUnreadable, IReadOnlyDictionary<Guid, string> storedActiveHashes)
    {
        if (anyUnreadable || pipelines.Count != storedActiveHashes.Count)
        {
            return true; // uncertain state, or a pipeline was added/removed/deactivated/reactivated.
        }

        foreach (var pipeline in pipelines)
        {
            if (!storedActiveHashes.TryGetValue(pipeline.Id, out var storedHash)
                || string.IsNullOrEmpty(storedHash)
                || !string.Equals(storedHash, pipeline.ContentHash, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reconciles this repo's pipeline rows, git-declared schedules, and declared pipeline-column rows
    /// from the phase-one preparation. Runs inside the sync's transaction and performs only database work.</summary>
    private static async Task<(int Added, int Updated, int Unchanged, int Deactivated)> ApplyPipelinesAsync(
        CatalogDbContext context, Guid repoId, DateTime nowUtc,
        IReadOnlyList<PreparedPipeline> pipelines, IReadOnlySet<Guid> presentIds,
        IReadOnlyList<PreparedSchedule> schedules, CancellationToken ct)
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
                row.RelativePath = Normalize(prepared.Flow.Node.File);
                unchanged++;
                continue;
            }

            var flow = prepared.Flow;
            var projected = CatalogProjection.Pipeline(
                repoId, flow.Node.Name, flow.Node.Kind, flow.Node.Batch, Normalize(flow.Node.File),
                flow.SourceServerRef, flow.TargetServerRef, prepared.ContentHash, prepared.Yaml, prepared.DefinitionJson, nowUtc);

            if (row is not null)
            {
                row.Name = projected.Name;
                row.Kind = projected.Kind;
                row.Batch = projected.Batch;
                row.RelativePath = projected.RelativePath;
                row.SourceServer = projected.SourceServer;
                row.TargetServer = projected.TargetServer;
                row.ContentHash = projected.ContentHash;
                row.Yaml = projected.Yaml;
                row.DefinitionJson = projected.DefinitionJson;
                row.Active = true;
                row.LastSeenUtc = nowUtc;
                updated++;
            }
            else
            {
                context.Pipelines.Add(projected);
                added++;
            }
        }

        var deactivated = 0;
        foreach (var (id, row) in existing)
        {
            if (!presentIds.Contains(id) && row.Active)
            {
                row.Active = false;
                deactivated++;
            }
        }

        // Stage the validated yaml schedule mirror: an operator's API pause is preserved and API-created
        // schedules are never touched; a flow whose schedule left git has its yaml schedule removed. Staged on
        // this context so the changes commit inside the sync's own transaction (the store's transaction-free
        // variants).
        var scheduleKeep = new HashSet<Guid>();
        foreach (var schedule in schedules)
        {
            var scheduleId = await ScheduleStore.StageYamlUpsertAsync(
                context, repoId, schedule.FlowName, schedule.Spec.Cron, schedule.Spec.IntervalSeconds,
                schedule.Spec.Timezone, schedule.Spec.Enabled, schedule.Spec.Catchup, schedule.NextFireUtc, nowUtc, ct).ConfigureAwait(false);
            scheduleKeep.Add(scheduleId);
        }

        await ScheduleStore.StageRemoveYamlSchedulesNotInAsync(context, repoId, scheduleKeep, ct).ConfigureAwait(false);

        // Replace this repo's declared pipeline-column rows from the authored YAML transforms of every present
        // flow (the source of truth for "which transforms are set"). Rebuilt wholesale for this repo so a removed
        // or edited transform does not linger; detected rows (from runs) are a different kind and are left
        // untouched. The rows project from the documents parsed in phase one, fresh per attempt.
        await context.PipelineColumns
            .Where(c => c.RepoId == repoId && c.Kind == PipelineColumnKinds.Declared)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var prepared in pipelines)
        {
            foreach (var column in ProjectDeclaredColumns(prepared.Document, repoId, prepared.Id))
            {
                context.PipelineColumns.Add(column);
            }
        }

        return (added, updated, unchanged, deactivated);
    }

    /// <summary>Projects a flow document's authored transform policy into declared column rows. File and
    /// relational (ing) flows carry the shared transform block; any other kind, or a document that failed to
    /// parse (null), contributes none (the pipeline projection already warned about a parse failure; declared
    /// columns are an enrichment, never a reason to fail the sync).</summary>
    private static IReadOnlyList<CatalogPipelineColumn> ProjectDeclaredColumns(FlowDocument? document, Guid repoId, Guid pipelineId)
    {
        var policy = document switch
        {
            FileFlowDocument file => file.Flow.Inference,
            IngestionFlowDocument ing => ing.Document.Flow.Transform,
            _ => null,
        };

        return policy is { Columns.Count: > 0 }
            ? CatalogProjection.PipelineColumnsDeclared(repoId, pipelineId, policy)
            : [];
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
                warnings.Add($"run artifact '{file}' could not be read ({SecretHygiene.RedactedMessage(ex.Message)}); skipped.");
                failed++;
            }
            finally
            {
                document?.Dispose();
            }
        }

        return (prepared, skipped, failed);
    }

    /// <summary>Inserts the phase-one runs and their drill-down detail, and applies the newest detected
    /// transform-view projection per pipeline. Runs inside the sync's transaction.</summary>
    private static async Task<RunSyncTally> ApplyRunsAsync(
        CatalogDbContext context, Guid repoId, IReadOnlyList<PreparedRun> runs, int skippedInScan, int failedInScan, CancellationToken ct)
    {
        var skipped = skippedInScan;
        var added = 0;
        var files = 0;
        var assertions = 0;
        var statements = 0;
        var surrogateKeys = 0;
        var metrics = 0;

        // The pre-transaction scan already dropped every run the catalog knew then; re-verify the survivors
        // INSIDE the transaction (in bounded chunks) so a run another node recorded in the meantime is skipped,
        // keeping the insert idempotent under concurrency.
        var alreadyKnown = (await SelectByKeysAsync(
                runs.Select(r => r.Run.RunId).ToList(),
                chunk => context.Runs.Where(r => chunk.Contains(r.RunId)).Select(r => r.RunId).ToListAsync(ct))
            .ConfigureAwait(false)).ToHashSet();

        // The newest transform-view projection seen per pipeline this pass: the detected pipeline columns are a
        // "latest run wins" snapshot, so only the most recent run's view columns are applied after the loop.
        var detectedCandidates = new Dictionary<Guid, (DateTime WrittenUtc, IReadOnlyList<CatalogPipelineColumn> Rows)>();

        foreach (var prepared in runs)
        {
            ct.ThrowIfCancellationRequested();
            if (alreadyKnown.Contains(prepared.Run.RunId))
            {
                skipped++; // recorded by a concurrent sync/write-back since the scan; a run is immutable.
                continue;
            }

            // A run is immutable, so its drill-down detail is inserted exactly once, with the run itself.
            context.Runs.Add(prepared.Run);
            added++;
            var detail = AddRunDetail(context, prepared.Document.RootElement, prepared.Run.RunId, repoId);
            files += detail.Files;
            assertions += detail.Assertions;
            statements += detail.Statements;
            surrogateKeys += detail.SurrogateKeys;
            metrics += detail.Metrics;

            var detected = CatalogProjection.PipelineColumnsDetected(prepared.Document.RootElement, repoId, prepared.Run.PipelineId);
            if (detected.Count > 0
                && (!detectedCandidates.TryGetValue(prepared.Run.PipelineId, out var current) || prepared.Run.WrittenUtc > current.WrittenUtc))
            {
                detectedCandidates[prepared.Run.PipelineId] = (prepared.Run.WrittenUtc, detected);
            }
        }

        // Apply the newest detected view projection per pipeline, but never over a run the catalog already knows
        // that is newer than this pass's candidate (an old artifact synced late must not regress the snapshot).
        foreach (var (pipelineId, candidate) in detectedCandidates)
        {
            var newerKnown = await context.Runs
                .AnyAsync(r => r.PipelineId == pipelineId && r.WrittenUtc > candidate.WrittenUtc, ct).ConfigureAwait(false);
            if (newerKnown)
            {
                continue;
            }

            await context.PipelineColumns
                .Where(c => c.PipelineId == pipelineId && c.Kind == PipelineColumnKinds.Detected)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            foreach (var row in candidate.Rows)
            {
                context.PipelineColumns.Add(row);
            }
        }

        return new RunSyncTally(added, skipped, failedInScan, files, assertions, statements, surrogateKeys, metrics);
    }

    /// <summary>Adds the immutable drill-down detail of one run (files, assertions, generated SQL, surrogate keys,
    /// health-check metrics) projected from its run.json root. Shared by the full estate sync and the per-run
    /// write-back, so the detail projection is wired in exactly one place.</summary>
    internal static (int Files, int Assertions, int Statements, int SurrogateKeys, int Metrics) AddRunDetail(
        CatalogDbContext context, JsonElement root, Guid runId, Guid repoId)
    {
        var files = 0;
        var assertions = 0;
        var statements = 0;
        var surrogateKeys = 0;
        var metrics = 0;

        foreach (var runFile in CatalogProjection.RunFiles(root, runId, repoId))
        {
            context.RunFiles.Add(runFile);
            files++;
        }

        foreach (var assertion in CatalogProjection.RunAssertions(root, runId, repoId))
        {
            context.RunAssertions.Add(assertion);
            assertions++;
        }

        foreach (var statement in CatalogProjection.RunStatements(root, runId, repoId))
        {
            context.RunStatements.Add(statement);
            statements++;
        }

        foreach (var surrogateKey in CatalogProjection.RunSurrogateKeys(root, runId, repoId))
        {
            context.RunSurrogateKeys.Add(surrogateKey);
            surrogateKeys++;
        }

        foreach (var metric in CatalogProjection.RunHealthCheckMetrics(root, runId, repoId))
        {
            context.RunHealthCheckMetrics.Add(metric);
            metrics++;
        }

        return (files, assertions, statements, surrogateKeys, metrics);
    }

    /// <summary>
    /// The self-maintaining write-back: records ONE just-completed run (and ensures its pipeline row) into the
    /// catalog, so a configured database stays current without a manual full <see cref="SyncAsync"/>. It upserts
    /// the repo and the single flow that produced the run, then inserts the run and its detail (idempotent: a run
    /// already recorded is left untouched, since a run is immutable). It deliberately does NOT recompute lineage
    /// or scan sibling flows: the execution plan and the cross-repo graph stay the full sync's responsibility, so
    /// the per-run cost is bounded. Runs in one serializable transaction; the caller treats any failure as
    /// non-fatal to the run itself.
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
            ?? throw new SqlFlow.Core.SqlFlowException($"'{flowFilePath}' has no parent directory.");
        var repoId = FlowIdentity.FromName(repoName);
        // One serializable transaction, run through the context's execution strategy so it is a single retriable
        // unit. The control plane enables connection resiliency (EnableRetryOnFailure); EF then forbids a
        // user-initiated transaction unless it is wrapped this way. The work rebuilds all its state from the flow
        // document and run.json each attempt, so a retry is safe. See InSerializableTransactionAsync.
        return await CatalogTransaction.InSerializableAsync(context, async () =>
        {
            var warnings = new List<string>();
            await UpsertRepoAsync(context, repoId, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
            var pipelineChange = await UpsertSinglePipelineAsync(context, root, fullFlowPath, repoId, nowUtc, warnings, ct).ConfigureAwait(false);

            var runRecorded = false;
            var detail = (Files: 0, Assertions: 0, Statements: 0, SurrogateKeys: 0, Metrics: 0);

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
                        detail = AddRunDetail(context, document.RootElement, run.RunId, repoId);

                        // Refresh the pipeline's detected view projection from this run ("latest run wins"),
                        // unless the catalog already knows a newer run for the pipeline (a stale artifact
                        // recorded late must not regress the snapshot).
                        var detected = CatalogProjection.PipelineColumnsDetected(document.RootElement, repoId, run.PipelineId);
                        if (detected.Count > 0)
                        {
                            var newerKnown = await context.Runs
                                .AnyAsync(r => r.PipelineId == run.PipelineId && r.WrittenUtc > run.WrittenUtc, ct).ConfigureAwait(false);
                            if (!newerKnown)
                            {
                                await context.PipelineColumns
                                    .Where(c => c.PipelineId == run.PipelineId && c.Kind == PipelineColumnKinds.Detected)
                                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                                foreach (var row in detected)
                                {
                                    context.PipelineColumns.Add(row);
                                }
                            }
                        }
                    }

                    // else: already recorded (a prior full sync or write-back); a run is immutable, so nothing to do.
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"run artifact '{runJsonPath}' could not be read ({SecretHygiene.RedactedMessage(ex.Message)}); not recorded.");
            }

            return new RecordRunResult
            {
                PipelineChange = pipelineChange,
                RunRecorded = runRecorded,
                RunFilesAdded = detail.Files,
                RunAssertionsAdded = detail.Assertions,
                RunStatementsAdded = detail.Statements,
                RunSurrogateKeysAdded = detail.SurrogateKeys,
                RunHealthCheckMetricsAdded = detail.Metrics,
                Warnings = warnings,
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Upserts the single flow that produced a run by loading and parsing JUST that document (a targeted
    /// read, never an estate scan), reusing the same redaction, hashing, and projection as the full pipeline
    /// sync. Lineage-derived fields (Wave) are left to the full sync; an unchanged flow only re-affirms its
    /// presence. A missing, unreadable, or unparseable document is a warning and the run is recorded without a
    /// pipeline row.</summary>
    private async Task<PipelineChange> UpsertSinglePipelineAsync(
        CatalogDbContext context, string root, string fullFlowPath, Guid repoId, DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var relativePath = Path.GetRelativePath(root, fullFlowPath);
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
        catch (SqlFlow.Core.SqlFlowException ex)
        {
            warnings.Add($"'{fullFlowPath}' could not be parsed as a flow ({SecretHygiene.RedactedMessage(ex.Message)}); its run is recorded without a pipeline row.");
            return PipelineChange.None;
        }

        if (ProjectHeader(document) is not { } header)
        {
            warnings.Add($"'{fullFlowPath}' is an orchestration document, not a runnable flow; its run is recorded without a pipeline row.");
            return PipelineChange.None;
        }

        if (SecretHygiene.LooksLikeEmbeddedSecret(rawYaml))
        {
            warnings.Add($"'{header.Name}' ({relativePath}) appears to embed a credential; it is redacted in the catalog, but secrets must be ${{env:...}}/${{keyvault:...}} references in the YAML, not literals.");
        }

        var yaml = SecretHygiene.RedactedMessage(rawYaml);
        var hash = CatalogProjection.Hash(yaml);
        var id = CatalogIdentity.Pipeline(repoId, header.Name);
        var row = await context.Pipelines.FindAsync([id], ct).ConfigureAwait(false);

        // Refresh this one pipeline's declared columns from its YAML on every write-back: declared columns are the
        // source-of-truth projection, cheap to rebuild for a single flow, and this keeps a run-only node's catalog
        // current with authored transforms without waiting for a full sync. Replaced by (pipeline, declared).
        await context.PipelineColumns
            .Where(c => c.PipelineId == id && c.Kind == PipelineColumnKinds.Declared)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var column in ProjectDeclaredColumns(document, repoId, id))
        {
            context.PipelineColumns.Add(column);
        }

        if (row is not null && string.Equals(row.ContentHash, hash, StringComparison.Ordinal))
        {
            row.Active = true;
            row.LastSeenUtc = nowUtc;
            row.RelativePath = Normalize(relativePath);
            return PipelineChange.Unchanged;
        }

        var definitionJson = SerializeDefinition(document, fullFlowPath, warnings);
        var projected = CatalogProjection.Pipeline(
            repoId, header.Name, header.Kind, header.Batch, Normalize(relativePath),
            header.SourceServer, header.TargetServer, hash, yaml, definitionJson, nowUtc);

        if (row is not null)
        {
            row.Name = projected.Name;
            row.Kind = projected.Kind;
            row.Batch = projected.Batch;
            row.RelativePath = projected.RelativePath;
            row.SourceServer = projected.SourceServer;
            row.TargetServer = projected.TargetServer;
            row.ContentHash = projected.ContentHash;
            row.Yaml = projected.Yaml;
            row.DefinitionJson = projected.DefinitionJson;
            row.Active = true;
            row.LastSeenUtc = nowUtc;
            return PipelineChange.Updated;
        }

        context.Pipelines.Add(projected);
        return PipelineChange.Added;
    }

    /// <summary>The pipeline-projection header of one flow document: the fields a catalog row derives from the
    /// document itself, shaped exactly like the estate scan's flow nodes.</summary>
    private sealed record FlowHeader(string Name, string Kind, string? Batch, string? SourceServer, string? TargetServer);

    /// <summary>Projects one already-loaded document into its pipeline header, mirroring how
    /// <see cref="FlowSetCollector"/> shapes each kind's flow node (name, kind, batch, server identities), so the
    /// per-run write-back can upsert a pipeline from a single targeted load. Null for an orchestration document
    /// (scm/batch), which the full sync never projects as a pipeline either.</summary>
    private static FlowHeader? ProjectHeader(FlowDocument document)
    {
        switch (document)
        {
            case IngestionFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return new FlowHeader(
                    flow.SysAlias ?? flow.Target.Table.Name, "ing", flow.Batch,
                    ServerIdentity.From(refs[flow.Source.Server]), ServerIdentity.From(refs[flow.Target.Server]));
            }

            case ExportFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                var server = ServerIdentity.From(refs[flow.SrcServer]);
                return new FlowHeader(flow.SysAlias, "exp", flow.Batch, server, server);
            }

            case StoredProcedureFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return new FlowHeader(flow.SysAlias, "sp", flow.Batch, null, ServerIdentity.From(refs[flow.Server]));
            }

            case HealthCheckFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return new FlowHeader(flow.SysAlias, "hc", flow.Batch, null, ServerIdentity.From(refs[flow.Server]));
            }

            case FileFlowDocument doc:
                return new FlowHeader(
                    doc.Flow.Name, "file", doc.Flow.Batch, null, ServerIdentity.From(doc.Flow.Target.Connection));

            case InvokeFlowDocument doc:
                return new FlowHeader(
                    doc.Document.Definition.InvokeAlias, "inv", doc.Document.Definition.Batch, null, ServerIdentity.FileSystem);

            default:
                // scm/batch (and any future orchestration kind): they move no catalog data and never become
                // pipeline rows.
                return null;
        }
    }

    private static Dictionary<string, string> ConnectionRefs(IEnumerable<Core.Connections.DataSource> connections)
        => connections.ToDictionary(c => c.Alias, c => c.ConnectionRef, StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes a precomputed lineage report into the catalog: the global object registry, the data
    /// dictionary, this repo's edges and flow dependencies, the execution waves, and the identity healing. Runs
    /// inside the sync's transaction and performs only database work; the report itself was computed before the
    /// transaction opened.</summary>
    private static async Task<(int Objects, int Superseded, int Columns, int Edges, int FlowDeps, int Waves, bool Connected)> ApplyLineageAsync(
        CatalogDbContext context, Guid repoId, LineageReport report, bool includeDerived, DateTime nowUtc, CancellationToken ct)
    {
        // Objects are GLOBAL (shared across repos by their canonical key) - upsert, never delete. Load only the
        // keys this report mentions, so the working set scales with the report, not the whole catalog.
        var keys = report.Objects.Select(o => o.Key).Distinct().ToList();
        // AsTracking so the object-metadata updates below persist under a NoTracking host context (see the pipeline
        // query in ApplyPipelinesAsync); harmless on a tracking context.
        var existingObjects = (await SelectByKeysAsync(
                keys, chunk => context.Objects.Where(o => chunk.Contains(o.Key)).AsTracking().ToListAsync(ct))
            .ConfigureAwait(false)).ToDictionary(o => o.Key);
        foreach (var node in report.Objects)
        {
            if (existingObjects.TryGetValue(node.Key, out var row))
            {
                row.ServerRef = node.ServerRef;
                row.Database = NullIfBlank(node.Database);
                row.Schema = NullIfBlank(node.Schema);
                row.Name = node.Name;
                row.Kind = node.Kind.ToString();
                // Only the derived tier reads a module body; an offline sync must not null a stored definition.
                // When present it is redacted (a body can embed a literal credential) and normalized the same way
                // as a freshly-inserted object (blank -> null).
                if (node.Definition is not null)
                {
                    row.Definition = NullIfBlank(SecretHygiene.RedactedMessage(node.Definition));
                }

                // The generating DDL, likewise: only overwrite when this sync captured a script, so an offline
                // sync that saw no run trace never wipes a script an earlier sync stored.
                if (node.Script is not null)
                {
                    row.Script = NullIfBlank(SecretHygiene.RedactedMessage(node.Script));
                    row.ScriptTier = node.ScriptTier?.ToString();
                    row.ScriptUpdatedUtc = nowUtc;
                }

                row.LastSeenUtc = nowUtc;
            }
            else
            {
                context.Objects.Add(CatalogProjection.MapObject(node, nowUtc));
            }
        }

        // Object columns are the data dictionary. Refresh ONLY the objects a tier supplied columns for this
        // sync (the node carries columns); a failed or offline collection (no columns) never wipes a
        // previously-collected dictionary. Replace-by-key so a re-read reflects schema changes. Precedence:
        // an offline (Observed) set must not overwrite a live (Derived) set an earlier connected sync stored,
        // so a key is refreshed only when this sync's tier is at least as authoritative as what is on record.
        // Like the global Object/Definition (upsert-only, never deleted for cross-repo identity), a dropped
        // object's columns are NOT cleaned up here; the dictionary is additive and tolerates that staleness.
        var columns = 0;
        var withColumns = report.Objects.Where(o => o.Columns.Count > 0).ToList();
        if (withColumns.Count > 0)
        {
            var candidateKeys = withColumns.Select(o => o.Key).Distinct().ToList();
            // Distinct per chunk is a true distinct: the chunks partition the keys, so no pair repeats.
            var existingTiers = await SelectByKeysAsync(
                    candidateKeys,
                    chunk => context.ObjectColumns
                        .Where(c => chunk.Contains(c.ObjectKey))
                        .Select(c => new { c.ObjectKey, c.Tier })
                        .Distinct()
                        .ToListAsync(ct))
                .ConfigureAwait(false);
            var existingRankByKey = existingTiers
                .GroupBy(x => x.ObjectKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(x => TierRank(x.Tier)), StringComparer.Ordinal);

            var nodesToRefresh = withColumns
                .Where(o => TierRank((o.ColumnsTier ?? Core.Lineage.LineageTier.Observed).ToString())
                            >= (existingRankByKey.TryGetValue(o.Key, out var rank) ? rank : -1))
                .ToList();
            var refreshedKeys = nodesToRefresh.Select(o => o.Key).Distinct().ToList();
            if (refreshedKeys.Count > 0)
            {
                await ExecuteByKeysAsync(
                        refreshedKeys, chunk => context.ObjectColumns.Where(c => chunk.Contains(c.ObjectKey)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
                foreach (var node in nodesToRefresh)
                {
                    var tier = (node.ColumnsTier ?? Core.Lineage.LineageTier.Observed).ToString();
                    foreach (var column in node.Columns)
                    {
                        context.ObjectColumns.Add(new CatalogObjectColumn
                        {
                            ObjectKey = node.Key,
                            Ordinal = column.Ordinal,
                            Name = column.Name,
                            DataType = column.DataType,
                            Nullable = column.Nullable,
                            Tier = tier,
                        });
                        columns++;
                    }
                }
            }
        }

        // Edges are this repo's view of the graph: replace them wholesale so a removed flow's edges do not linger.
        await context.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var objectNames = report.Objects.ToDictionary(o => o.Key, o => o.Name, StringComparer.Ordinal);
        var edges = 0;
        foreach (var edge in report.Edges)
        {
            var name = objectNames.TryGetValue(edge.ObjectKey, out var n) ? n : edge.ObjectKey;
            context.LineageEdges.Add(CatalogProjection.Edge(edge, repoId, name));
            edges++;
        }

        // Identity healing: a key that now carries its database (or schema) supersedes the weaker key an
        // earlier sync recorded for the SAME object (a file-flow target keyed '<server>||<schema>|<name>'
        // before default-database resolution, for example). Objects are global and upsert-only for
        // cross-repo identity, but a weaker twin is not another repo's object, it is this object under a
        // partial key; delete it once no repo's edges reference it, so the explorer stops listing a
        // columnless duplicate. This repo's replacement edges above only reference report keys, which are
        // excluded here, so a twin still live in THIS report is never deleted.
        var weakerTwins = report.Objects
            .SelectMany(o => new[]
            {
                string.IsNullOrWhiteSpace(o.Database) ? null : NodeKey.For(o.ServerRef, null, o.Schema, o.Name),
                string.IsNullOrWhiteSpace(o.Schema) ? null : NodeKey.For(o.ServerRef, o.Database, null, o.Name),
            })
            .OfType<string>()
            .Where(twin => !objectNames.ContainsKey(twin))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var superseded = 0;
        if (weakerTwins.Count > 0)
        {
            var stillReferenced = await SelectByKeysAsync(
                    weakerTwins,
                    chunk => context.LineageEdges
                        .Where(e => chunk.Contains(e.ObjectKey))
                        .Select(e => e.ObjectKey)
                        .Distinct()
                        .ToListAsync(ct))
                .ConfigureAwait(false);
            var deletable = weakerTwins.Except(stillReferenced, StringComparer.Ordinal).ToList();
            if (deletable.Count > 0)
            {
                superseded = await ExecuteByKeysAsync(
                        deletable, chunk => context.Objects.Where(o => chunk.Contains(o.Key)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
                await ExecuteByKeysAsync(
                        deletable, chunk => context.ObjectColumns.Where(c => chunk.Contains(c.ObjectKey)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
            }
        }

        // The execution plan - lineage's primary output. Stamp each pipeline with its wave (its batch and order)
        // and replace this repo's flow-level dependency edges, so a GUI/orchestrator reads the runnable order.
        foreach (var wave in report.ExecutionPlan.Waves)
        {
            foreach (var flow in wave.Flows)
            {
                var pipeline = await context.Pipelines.FindAsync([CatalogIdentity.Pipeline(repoId, flow)], ct).ConfigureAwait(false);
                if (pipeline is not null)
                {
                    pipeline.Wave = wave.Wave;
                }
            }
        }

        await context.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var dependencies = 0;
        foreach (var dependency in report.FlowDependencies)
        {
            var via = string.Join(", ", dependency.ViaObjects.Select(k => objectNames.TryGetValue(k, out var n) ? n : k));
            context.FlowDependencies.Add(CatalogProjection.FlowDependency(dependency, repoId, via));
            dependencies++;
        }

        return (report.Objects.Count, superseded, columns, edges, dependencies, report.ExecutionPlan.Waves.Count, includeDerived);
    }

    /// <summary>The authority ordering of a column/definition tier: Derived (live) beats Observed (parsed from
    /// a run) beats Declared. An unknown or blank tier ranks below all, so it never blocks a real refresh.</summary>
    private static int TierRank(string? tier) => tier switch
    {
        nameof(Core.Lineage.LineageTier.Derived) => 2,
        nameof(Core.Lineage.LineageTier.Observed) => 1,
        nameof(Core.Lineage.LineageTier.Declared) => 0,
        _ => -1,
    };

    /// <summary>Runs a keyed membership query per <see cref="KeyChunkSize"/> chunk and unions the rows, so a
    /// large key set never approaches SQL Server's per-command parameter limit. The chunks partition the keys,
    /// so per-chunk results combine without cross-chunk duplicates.</summary>
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

    /// <summary>Runs a keyed bulk write per <see cref="KeyChunkSize"/> chunk and sums the affected row counts.</summary>
    private static async Task<int> ExecuteByKeysAsync<TKey>(IReadOnlyList<TKey> keys, Func<TKey[], Task<int>> write)
    {
        var affected = 0;
        foreach (var chunk in keys.Chunk(KeyChunkSize))
        {
            affected += await write(chunk).ConfigureAwait(false);
        }

        return affected;
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
            warnings.Add($"'{fullPath}' could not be serialized for the catalog definition ({SecretHygiene.RedactedMessage(ex.Message)}); stored without it.");
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
            warnings.Add($"'{relativePath}' could not be read ({SecretHygiene.RedactedMessage(ex.Message)}); left unchanged.");
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
