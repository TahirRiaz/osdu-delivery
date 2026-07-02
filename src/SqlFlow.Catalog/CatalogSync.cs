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
/// their own id, so re-syncing the same folders, or aggregating many nodes' folders, is idempotent. The whole
/// pass runs in one serializable transaction so two syncs of the same repo cannot lose each other's updates.
/// Secrets never rest in the catalog: a flow document is detected (and warned) when it embeds a credential, and
/// the stored YAML / definition JSON are passed through the same redactor used for connection-string error text.
/// </summary>
public sealed class CatalogSync
{
    // A flow document is kilobytes; a run.json is small. These caps stop a hostile or corrupt file from
    // exhausting memory or bloating the nvarchar(max) columns. Over-limit files are skipped with a warning.
    private const long MaxYamlBytes = 16L * 1024 * 1024;
    internal const long MaxRunJsonBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions DefinitionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FlowSetCollector _estate = new();

    private readonly YamlFlowLoader _flowLoader = new();

    private readonly YamlIngestionFlowLoader _ingestionLoader = new();

    private readonly YamlDocumentLoader _documents = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader());

    public async Task<CatalogSyncResult> SyncAsync(
        CatalogDbContext context, string estateDirectory, string repoName, string? repoRemoteUrl, DateTime nowUtc,
        bool includeDerived = false, ISecretResolver? secrets = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(estateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        var root = Path.GetFullPath(estateDirectory);

        // One serializable transaction for the whole pass (so concurrent syncs of the same repo serialize instead
        // of racing), run through the context's execution strategy so it is a single retriable unit. The control
        // plane enables connection resiliency (EnableRetryOnFailure); EF then forbids a user-initiated transaction
        // unless wrapped this way. The pass re-collects the estate from disk each attempt, so a retry is safe.
        return await CatalogTransaction.InSerializableAsync(context, async () =>
        {
            var warnings = new List<string>();
            var repoId = await UpsertRepoAsync(context, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
            var pipelines = await SyncPipelinesAsync(context, root, repoId, nowUtc, warnings, ct).ConfigureAwait(false);
            var runs = await SyncRunsAsync(context, root, repoId, warnings, ct).ConfigureAwait(false);
            var lineage = await SyncLineageAsync(context, root, repoId, includeDerived, secrets, nowUtc, warnings, ct).ConfigureAwait(false);

            return new CatalogSyncResult
            {
                PipelinesAdded = pipelines.Added,
                PipelinesUpdated = pipelines.Updated,
                PipelinesUnchanged = pipelines.Unchanged,
                PipelinesDeactivated = pipelines.Deactivated,
                RunsAdded = runs.Added,
                RunsSkipped = runs.Skipped,
                RunsFailed = runs.Failed,
                RunFilesAdded = runs.Files,
                RunAssertionsAdded = runs.Assertions,
                RunStatementsAdded = runs.Statements,
                RunSurrogateKeysAdded = runs.SurrogateKeys,
                RunHealthCheckMetricsAdded = runs.Metrics,
                ObjectsUpserted = lineage.Objects,
                ObjectColumns = lineage.Columns,
                LineageEdges = lineage.Edges,
                FlowDependencies = lineage.FlowDeps,
                Waves = lineage.Waves,
                LineageConnected = lineage.Connected,
                Warnings = warnings,
            };
        }, ct).ConfigureAwait(false);
    }

    // The retriable serializable-transaction wrapper lives in CatalogTransaction so the run-queue lifecycle shares
    // the exact same execution-strategy + change-tracker-reset semantics as the sync/write-back.

    private static async Task<Guid> UpsertRepoAsync(
        CatalogDbContext context, string repoName, string? remoteUrl, string root, DateTime nowUtc, CancellationToken ct)
    {
        var repoId = FlowIdentity.FromName(repoName);
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

        return repoId;
    }

    private async Task<(int Added, int Updated, int Unchanged, int Deactivated)> SyncPipelinesAsync(
        CatalogDbContext context, string root, Guid repoId, DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var collected = _estate.Collect(root);
        warnings.AddRange(collected.Warnings);

        // Only this repo's pipelines: another repo's flows in the same catalog must not be touched by this sync.
        var existing = await context.Pipelines.Where(p => p.RepoId == repoId).ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);
        var present = new HashSet<Guid>();
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var flow in collected.Flows)
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

            if (existing.TryGetValue(id, out var row) && string.Equals(row.ContentHash, hash, StringComparison.Ordinal))
            {
                // Unchanged content (derived fields cannot have changed either): re-affirm presence only.
                row.Active = true;
                row.LastSeenUtc = nowUtc;
                row.RelativePath = Normalize(flow.Node.File);
                unchanged++;
                continue;
            }

            var definitionJson = SerializeDefinition(fullPath, warnings);
            var projected = CatalogProjection.Pipeline(
                repoId, flow.Node.Name, flow.Node.Kind, flow.Node.Batch, Normalize(flow.Node.File),
                flow.SourceServerRef, flow.TargetServerRef, hash, yaml, definitionJson, nowUtc);

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
            if (!present.Contains(id) && row.Active)
            {
                row.Active = false;
                deactivated++;
            }
        }

        // Mirror git-declared schedules into the schedule table (the 'yaml' source). A flow's schedule lives in its
        // YAML and is refreshed from git on each sync, but an operator's API pause is preserved and API-created
        // schedules are never touched; a flow whose schedule left git has its yaml schedule removed. Staged on this
        // context so the changes commit inside the sync's own transaction (the store's transaction-free variants).
        var scheduledPipelineIds = new HashSet<Guid>();
        var scheduleKeep = new HashSet<Guid>();
        foreach (var flow in collected.Flows)
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
            var scheduleId = await ScheduleStore.StageYamlUpsertAsync(
                context, repoId, flow.Node.Name, spec.Cron, spec.IntervalSeconds, spec.Timezone, spec.Enabled,
                nextFire, nowUtc, ct).ConfigureAwait(false);
            scheduleKeep.Add(scheduleId);
        }

        await ScheduleStore.StageRemoveYamlSchedulesNotInAsync(context, repoId, scheduleKeep, ct).ConfigureAwait(false);

        // Project the authored per-column transforms of every present flow into the declared pipeline-column rows
        // (the source of truth for "which transforms are set"). Refreshed wholesale for this repo so a removed or
        // edited transform does not linger; detected rows (from runs) are a different kind and are left untouched.
        await RefreshDeclaredColumnsAsync(context, root, repoId, collected.Flows, present, ct).ConfigureAwait(false);

        return (added, updated, unchanged, deactivated);
    }

    /// <summary>
    /// Replaces this repo's declared pipeline-column rows from the authored YAML transforms of every present flow.
    /// Declared rows are the source-of-truth projection, so they are rebuilt wholesale (delete this repo's declared
    /// rows, re-insert) each full sync; detected rows (a different <see cref="PipelineColumnKinds"/>) are produced
    /// by runs and are never touched here. Only file flows carry authored transforms today (a FlowDefinition); a
    /// flow of any other kind contributes nothing.
    /// </summary>
    private async Task RefreshDeclaredColumnsAsync(
        CatalogDbContext context, string root, Guid repoId,
        IReadOnlyList<CollectedFlow> flows, HashSet<Guid> present, CancellationToken ct)
    {
        await context.PipelineColumns
            .Where(c => c.RepoId == repoId && c.Kind == PipelineColumnKinds.Declared)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var done = new HashSet<Guid>();
        foreach (var flow in flows)
        {
            var pipelineId = CatalogIdentity.Pipeline(repoId, flow.Node.Name);
            if (!present.Contains(pipelineId) || !done.Add(pipelineId))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, flow.Node.File));
            foreach (var column in ProjectDeclaredColumns(flow.Node.Kind, fullPath, repoId, pipelineId))
            {
                context.PipelineColumns.Add(column);
            }
        }
    }

    /// <summary>Loads a flow's transform policy and projects its authored transforms into declared column rows.
    /// File and relational (ing) flows carry the shared transform block; any other kind, or a document that fails
    /// to parse, contributes none (the pipeline projection already warned about a parse failure; declared columns
    /// are an enrichment, never a reason to fail the sync).</summary>
    private IReadOnlyList<CatalogPipelineColumn> ProjectDeclaredColumns(string kind, string fullPath, Guid repoId, Guid pipelineId)
    {
        try
        {
            var policy = kind switch
            {
                "file" => _flowLoader.LoadFile(fullPath).Inference,
                "ing" => _ingestionLoader.LoadFile(fullPath).Flow.Transform,
                _ => null,
            };

            if (policy is { Columns.Count: > 0 })
            {
                return CatalogProjection.PipelineColumnsDeclared(repoId, pipelineId, policy);
            }
        }
        catch (Exception ex) when (ex is SqlFlow.Core.SqlFlowException or IOException)
        {
            // A malformed document was already warned about by the pipeline projection.
        }

        return [];
    }

    private static async Task<RunSyncTally> SyncRunsAsync(
        CatalogDbContext context, string root, Guid repoId, List<string> warnings, CancellationToken ct)
    {
        // Scope the known-run set to this repo (a run id is globally unique and always synced under its own repo),
        // so the dedup memory grows with the repo, not the whole catalog.
        var known = (await context.Runs.Where(r => r.RepoId == repoId).Select(r => r.RunId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var seen = new HashSet<Guid>();
        var added = 0;
        var skipped = 0;
        var failed = 0;
        var files = 0;
        var assertions = 0;
        var statements = 0;
        var surrogateKeys = 0;
        var metrics = 0;

        // The newest transform-view projection seen per pipeline this pass: the detected pipeline columns are a
        // "latest run wins" snapshot, so only the most recent run's view columns are applied after the loop.
        var detectedCandidates = new Dictionary<Guid, (DateTime WrittenUtc, IReadOnlyList<CatalogPipelineColumn> Rows)>();

        foreach (var file in EnumerateRunArtifacts(root))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var length = new FileInfo(file).Length;
                if (length > MaxRunJsonBytes)
                {
                    warnings.Add($"run artifact '{file}' is {length} bytes, over the {MaxRunJsonBytes}-byte limit; skipped.");
                    failed++;
                    continue;
                }

                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                var run = CatalogProjection.RunFromJson(document.RootElement, repoId);
                if (run is null)
                {
                    warnings.Add($"run artifact '{file}' is missing required fields; skipped.");
                    failed++;
                    continue;
                }

                if (known.Contains(run.RunId) || !seen.Add(run.RunId))
                {
                    skipped++; // immutable and already recorded (or seen earlier this pass).
                    continue;
                }

                // A run is immutable, so its drill-down detail is inserted exactly once, with the run itself.
                context.Runs.Add(run);
                added++;
                var detail = AddRunDetail(context, document.RootElement, run.RunId, repoId);
                files += detail.Files;
                assertions += detail.Assertions;
                statements += detail.Statements;
                surrogateKeys += detail.SurrogateKeys;
                metrics += detail.Metrics;

                var detected = CatalogProjection.PipelineColumnsDetected(document.RootElement, repoId, run.PipelineId);
                if (detected.Count > 0
                    && (!detectedCandidates.TryGetValue(run.PipelineId, out var current) || run.WrittenUtc > current.WrittenUtc))
                {
                    detectedCandidates[run.PipelineId] = (run.WrittenUtc, detected);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"run artifact '{file}' could not be read ({SecretHygiene.RedactedMessage(ex.Message)}); skipped.");
                failed++;
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

        return new RunSyncTally(added, skipped, failed, files, assertions, statements, surrogateKeys, metrics);
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
        // One serializable transaction, run through the context's execution strategy so it is a single retriable
        // unit. The control plane enables connection resiliency (EnableRetryOnFailure); EF then forbids a
        // user-initiated transaction unless it is wrapped this way. The work rebuilds all its state from run.json
        // each attempt, so a retry is safe. See InSerializableTransactionAsync.
        return await CatalogTransaction.InSerializableAsync(context, async () =>
        {
            var warnings = new List<string>();
            var repoId = await UpsertRepoAsync(context, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
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

    /// <summary>Upserts the single flow that produced a run (found by its file path under <paramref name="root"/>),
    /// reusing the same redaction, hashing, and projection as the full pipeline sync. Lineage-derived fields
    /// (Wave) are left to the full sync; an unchanged flow only re-affirms its presence.</summary>
    private async Task<PipelineChange> UpsertSinglePipelineAsync(
        CatalogDbContext context, string root, string fullFlowPath, Guid repoId, DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var collected = _estate.Collect(root);
        var flow = collected.Flows.FirstOrDefault(f =>
            string.Equals(Path.GetFullPath(Path.Combine(root, f.Node.File)), fullFlowPath, StringComparison.OrdinalIgnoreCase));
        if (flow is null)
        {
            warnings.Add($"'{fullFlowPath}' was not found as a flow under '{root}'; its run is recorded without a pipeline row.");
            return PipelineChange.None;
        }

        var rawYaml = ReadYaml(fullFlowPath, flow.Node.File, warnings);
        if (rawYaml is null)
        {
            return PipelineChange.None;
        }

        if (SecretHygiene.LooksLikeEmbeddedSecret(rawYaml))
        {
            warnings.Add($"'{flow.Node.Name}' ({flow.Node.File}) appears to embed a credential; it is redacted in the catalog, but secrets must be ${{env:...}}/${{keyvault:...}} references in the YAML, not literals.");
        }

        var yaml = SecretHygiene.RedactedMessage(rawYaml);
        var hash = CatalogProjection.Hash(yaml);
        var id = CatalogIdentity.Pipeline(repoId, flow.Node.Name);
        var row = await context.Pipelines.FindAsync([id], ct).ConfigureAwait(false);

        // Refresh this one pipeline's declared columns from its YAML on every write-back: declared columns are the
        // source-of-truth projection, cheap to rebuild for a single flow, and this keeps a run-only node's catalog
        // current with authored transforms without waiting for a full sync. Replaced by (pipeline, declared).
        await context.PipelineColumns
            .Where(c => c.PipelineId == id && c.Kind == PipelineColumnKinds.Declared)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var column in ProjectDeclaredColumns(flow.Node.Kind, fullFlowPath, repoId, id))
        {
            context.PipelineColumns.Add(column);
        }

        if (row is not null && string.Equals(row.ContentHash, hash, StringComparison.Ordinal))
        {
            row.Active = true;
            row.LastSeenUtc = nowUtc;
            row.RelativePath = Normalize(flow.Node.File);
            return PipelineChange.Unchanged;
        }

        var definitionJson = SerializeDefinition(fullFlowPath, warnings);
        var projected = CatalogProjection.Pipeline(
            repoId, flow.Node.Name, flow.Node.Kind, flow.Node.Batch, Normalize(flow.Node.File),
            flow.SourceServerRef, flow.TargetServerRef, hash, yaml, definitionJson, nowUtc);

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

    private static async Task<(int Objects, int Columns, int Edges, int FlowDeps, int Waves, bool Connected)> SyncLineageAsync(
        CatalogDbContext context, string root, Guid repoId, bool includeDerived, ISecretResolver? secrets,
        DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        LineageReport report;
        try
        {
            report = await LineageService.ComputeAsync(
                new LineageOptions { FlowDirectory = root, IncludeObserved = true, IncludeDerived = includeDerived, Secrets = secrets },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlow.Core.SqlFlowException or IOException or InvalidOperationException)
        {
            // Lineage is an enrichment: a failure to compute it (e.g. a connect-tier timeout) must not fail the
            // whole sync. The pipeline registry and run history still land. But a wave from an earlier successful
            // sync no longer reflects this estate, so reset every active pipeline in this repo to the -1
            // "not computed" sentinel rather than leaving a stale wave that reads as a real execution order.
            // The pipelines are already tracked (loaded in SyncPipelinesAsync), so mutating them here is persisted
            // by the single SaveChangesAsync at the end of the pass.
            foreach (var pipeline in context.Pipelines.Local)
            {
                if (pipeline.RepoId == repoId && pipeline.Active)
                {
                    pipeline.Wave = -1;
                }
            }

            warnings.Add($"lineage was not computed for this sync ({SecretHygiene.RedactedMessage(ex.Message)}); objects and edges left unchanged, waves reset to not-computed.");
            return (0, 0, 0, 0, 0, false);
        }

        foreach (var warning in report.Warnings)
        {
            warnings.Add($"lineage: {warning}");
        }

        // Objects are GLOBAL (shared across repos by their canonical key) - upsert, never delete. Load only the
        // keys this report mentions, so the working set scales with the report, not the whole catalog.
        var keys = report.Objects.Select(o => o.Key).Distinct().ToList();
        var existingObjects = await context.Objects.Where(o => keys.Contains(o.Key)).ToDictionaryAsync(o => o.Key, ct).ConfigureAwait(false);
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

                row.LastSeenUtc = nowUtc;
            }
            else
            {
                context.Objects.Add(CatalogProjection.MapObject(node, nowUtc));
            }
        }

        // Object columns are the connected-tier data dictionary. Refresh ONLY the objects the derived tier
        // actually re-read this sync (the node carries columns); a failed or offline collection (no columns)
        // never wipes a previously-collected dictionary. Replace-by-key so a re-read reflects schema changes.
        // Like the global Object/Definition (upsert-only, never deleted for cross-repo identity), a dropped
        // object's columns are NOT cleaned up here; the dictionary is additive and tolerates that staleness.
        var columns = 0;
        var refreshedKeys = report.Objects.Where(o => o.Columns.Count > 0).Select(o => o.Key).Distinct().ToList();
        if (refreshedKeys.Count > 0)
        {
            await context.ObjectColumns.Where(c => refreshedKeys.Contains(c.ObjectKey)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            foreach (var node in report.Objects.Where(o => o.Columns.Count > 0))
            {
                foreach (var column in node.Columns)
                {
                    context.ObjectColumns.Add(new CatalogObjectColumn
                    {
                        ObjectKey = node.Key,
                        Ordinal = column.Ordinal,
                        Name = column.Name,
                        DataType = column.DataType,
                        Nullable = column.Nullable,
                    });
                    columns++;
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

        return (report.Objects.Count, columns, edges, dependencies, report.ExecutionPlan.Waves.Count, includeDerived);
    }

    private string SerializeDefinition(string fullPath, List<string> warnings)
    {
        try
        {
            var document = _documents.LoadFile(fullPath);
            var json = JsonSerializer.Serialize(document, document.GetType(), DefinitionJsonOptions);
            return SecretHygiene.RedactedMessage(json);
        }
        catch (Exception ex) when (ex is SqlFlow.Core.SqlFlowException or IOException or JsonException)
        {
            // FlowValidationException derives from SqlFlowException, so a malformed document is caught here too.
            warnings.Add($"'{fullPath}' could not be parsed for the catalog definition ({SecretHygiene.RedactedMessage(ex.Message)}); stored without it.");
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
