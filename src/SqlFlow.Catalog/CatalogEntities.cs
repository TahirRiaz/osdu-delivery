namespace SqlFlow.Catalog;

/// <summary>
/// The lifecycle states a <see cref="CatalogRun"/> moves through, stored as a short lowercase string so the value
/// is self-describing in the database and filterable with a plain equality predicate (no enum mapping). A
/// control-plane run is <c>queued</c> -> <c>running</c> -> <c>succeeded</c>/<c>failed</c>, or <c>cancelled</c> if
/// it is cancelled while still queued.
/// </summary>
public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>The terminal states; a run in one of these is finished and will not change.</summary>
    public static bool IsTerminal(string status)
        => status is Succeeded or Failed or Cancelled;
}

/// <summary>
/// One source repository synced into the catalog. Several git repos can sync into one catalog database, so the
/// GUI and queries span repos: every pipeline and run is attributed to its repo. Identity is stable from the
/// repo name, so re-syncing the same repo updates its row rather than duplicating it.
/// </summary>
public class CatalogRepo
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? RemoteUrl { get; set; }

    /// <summary>The local path that was synced (informational).</summary>
    public string? RootPath { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSyncUtc { get; set; }
}

/// <summary>
/// One pipeline in the shadow catalog: a YAML flow document from the git estate mapped into a row. Git/YAML is
/// the source of truth; this is a synchronized read-model for the GUI and for cross-file / cross-repo queries.
/// The hot dimensions (kind, batch, source/target server) are columns; the FULL definition is kept in
/// <see cref="DefinitionJson"/> (the parsed flow, normalized to JSON) so any field is queryable with SQL Server's
/// JSON functions across every file and repo, and in <see cref="Yaml"/> (the original text) for display. Both are
/// secret-redacted on the way in. <see cref="Id"/> is the flow's stable identity (the deterministic GUID the
/// engine derives from the name), so runs join to it with no run-time database round-trip.
/// </summary>
public class CatalogPipeline
{
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The flow kind: file / ing / exp / sp / inv / hc / scm / batch.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? Batch { get; set; }

    /// <summary>The flow document path relative to the repo root (forward-slashed).</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The source connection/server reference, for display and grouping; null when not applicable.</summary>
    public string? SourceServer { get; set; }

    /// <summary>The target connection/server reference, for display and grouping; null when not applicable.</summary>
    public string? TargetServer { get; set; }

    /// <summary>SHA-256 of the YAML text, so a sync can tell whether a flow changed.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The original YAML document text (secret-redacted), for display and full-text search.</summary>
    public string Yaml { get; set; } = string.Empty;

    /// <summary>The parsed flow normalized to JSON (secret-redacted): the queryable form of the whole definition,
    /// reachable field-by-field through SQL Server's JSON_VALUE / OPENJSON across all files and repos.</summary>
    public string DefinitionJson { get; set; } = string.Empty;

    /// <summary>True when the flow was present in the estate on the last sync; false once it leaves git (soft
    /// deactivation, so its run history stays attributable).</summary>
    public bool Active { get; set; }

    /// <summary>
    /// The flow's execution wave within its repo, computed by lineage: pipelines in the same wave have no
    /// dependency between them and can run together (a concurrency batch); a later wave runs only after every
    /// earlier wave finishes. -1 until lineage has been computed for the repo. This is lineage's primary output:
    /// the batches and the order pipelines run in.
    /// </summary>
    public int Wave { get; set; } = -1;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One execution in the shadow catalog: an on-disk <c>run.json</c> artifact mapped into a row, attributed to its
/// repo so run history aggregates across nodes and repos. Header fields follow the stable run.json contract;
/// metric fields are best-effort from the kind-specific result. Keyed by the run's own id, so re-syncing the same
/// folders (or many nodes' folders) is idempotent.
/// </summary>
public class CatalogRun
{
    public Guid RunId { get; set; }

    /// <summary>The owning pipeline's stable id (FlowIdentity of the flow name); a soft link (no FK), so a run can
    /// outlive its pipeline leaving the estate.</summary>
    public Guid PipelineId { get; set; }

    public Guid? RepoId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string FlowKind { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>
    /// The run's lifecycle state (see <see cref="RunStatuses"/>): a control-plane trigger inserts the run as
    /// <c>queued</c>; the worker atomically claims it (<c>running</c>) and completes it (<c>succeeded</c> /
    /// <c>failed</c>); a queued run can be <c>cancelled</c> before it is claimed. A run recorded straight from an
    /// on-disk artifact (a CLI run synced later) is born <c>succeeded</c> / <c>failed</c> from its result.
    /// </summary>
    public string Status { get; set; } = RunStatuses.Succeeded;

    /// <summary>When the control plane enqueued the run; null for a run recorded straight from its artifact.</summary>
    public DateTime? EnqueuedUtc { get; set; }

    /// <summary>The worker/node that claimed the run for execution; null until claimed (and for an artifact-sourced
    /// run). The basis for crash recovery: a run left <c>running</c> by a node that died is requeued.</summary>
    public string? ClaimedByNode { get; set; }

    /// <summary>The pool this run is routed to: only a worker that serves this pool may claim it. Null means
    /// "any node" (untargeted) - any worker claims it. Routing keeps a flow on a node that can actually reach its
    /// database and resolve its secrets (least privilege), so an on-prem flow runs on an on-prem node and a cloud
    /// flow on a cloud node.</summary>
    public string? TargetPool { get; set; }

    /// <summary>The git commit the run should execute, when it is pinned to one: the node materializes the repo at
    /// this exact SHA and runs the flow from there, so the run is reproducible and a node can run a flow it has no
    /// local copy of. Null runs the flow from the node's locally synced repo path (the default).</summary>
    public string? CommitSha { get; set; }

    public int SchemaVersion { get; set; }

    public DateTime WrittenUtc { get; set; }

    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    public double? DurationSeconds { get; set; }

    public long? RowsLoaded { get; set; }

    public long? RowsInserted { get; set; }

    public long? RowsUpdated { get; set; }

    public long? RowsDeleted { get; set; }

    public string? Error { get; set; }

    /// <summary>The host that produced the run, when the artifact records it (null until run.json carries it).</summary>
    public string? Host { get; set; }
}

/// <summary>
/// One object in the lineage graph: a table/view/procedure/function or a file endpoint. GLOBAL, not repo-scoped:
/// the key is the canonical identity (server reference + database + schema + name), so the SAME physical object
/// referenced by flows in different repos is ONE row. That shared identity is the join point for multi-repo
/// traceability - "what touches dbo.Customer" spans every repo. With the derived tier (a connected sync) the
/// <see cref="Kind"/> and metadata come from the live catalog; offline it is what the YAML declares.
/// </summary>
public class CatalogObject
{
    public string Key { get; set; } = string.Empty;

    /// <summary>A database-generated integer surrogate whose only purpose is to be the single-column,
    /// non-nullable, unique KEY INDEX a SQL Server full-text index requires: <see cref="Key"/> itself is too wide
    /// (nvarchar(900)) to be a full-text key. Not used by the application; the canonical identity is still
    /// <see cref="Key"/>.</summary>
    public long FullTextKey { get; set; }

    /// <summary>The connection reference the object was reached through (a ${env:..}/@alias/redacted identity).</summary>
    public string ServerRef { get; set; } = string.Empty;

    public string? Database { get; set; }

    public string? Schema { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Table / View / Procedure / Function / Trigger / Synonym / File / Unknown.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The module body (the <c>sys.sql_modules</c> definition) for a view/procedure/function/trigger,
    /// captured by the derived (connected) tier so the catalog is searchable code: "the proc whose body
    /// references dbo.Orders". Null for plain tables, an unconnected sync, or an encrypted module.</summary>
    public string? Definition { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One attributed lineage fact: a flow (or, in the derived tier, a module body) relating to an object. Repo-scoped
/// and fully replaced for a repo on each sync, so removed edges do not linger. The <see cref="ObjectKey"/> links
/// to a global <see cref="CatalogObject"/>; a GUI answers "what reads/writes object X across all repos" by
/// querying edges on that key, and "what does flow Y touch" by querying on <see cref="PipelineId"/>.
/// </summary>
public class CatalogLineageEdge
{
    public long Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The flow the fact belongs to (null for a module-derived fact).</summary>
    public string? Flow { get; set; }

    /// <summary>The repo-scoped pipeline id for <see cref="Flow"/> (null for a module-derived fact), for joining.</summary>
    public Guid? PipelineId { get; set; }

    /// <summary>The module (view/proc/function key) whose body produced this fact (derived tier; null otherwise).</summary>
    public string? ViaModule { get; set; }

    /// <summary>Reads / Writes / Creates / Requires / Destroys.</summary>
    public string Relation { get; set; } = string.Empty;

    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>The object's display name, denormalized for listing without a join.</summary>
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>Declared / Observed / Derived: the provenance of the fact.</summary>
    public string Tier { get; set; } = string.Empty;
}

/// <summary>
/// One flow-level dependency in a repo's execution plan: <see cref="ToFlow"/> must wait for <see cref="FromFlow"/>
/// because of the objects one writes and the other reads. This is the edge set behind the waves; together with
/// <see cref="CatalogPipeline.Wave"/> it is the executable order of the estate's pipelines. Repo-scoped and fully
/// replaced on each sync.
/// </summary>
public class CatalogFlowDependency
{
    public long Id { get; set; }

    public Guid RepoId { get; set; }

    public string FromFlow { get; set; } = string.Empty;

    public string ToFlow { get; set; } = string.Empty;

    public Guid FromPipelineId { get; set; }

    public Guid ToPipelineId { get; set; }

    /// <summary>The object names that mediate the dependency, comma-joined for display.</summary>
    public string ViaObjects { get; set; } = string.Empty;
}

/// <summary>One file a run processed (file flows): the drill-down detail under a <see cref="CatalogRun"/>.</summary>
public class CatalogRunFile
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public Guid? RepoId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Path { get; set; }

    public long Rows { get; set; }

    public int Columns { get; set; }

    public long SizeBytes { get; set; }
}

/// <summary>One data-quality assertion a run evaluated: the drill-down detail under a <see cref="CatalogRun"/>.</summary>
public class CatalogRunAssertion
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public Guid? RepoId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The assertion's first-column result string ("" / a value / "0" on error).</summary>
    public string Result { get; set; } = string.Empty;

    public string AssertedValue { get; set; } = string.Empty;

    /// <summary>True when the assertion ran without an evaluation error.</summary>
    public bool Evaluated { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One generated SQL statement a run executed, in execution order: the central, queryable trace log. Every
/// statement the engine produced for a run (the kind-specific <c>sqlTrace</c>, a file flow's <c>ddlExecuted</c>,
/// and surrogate-key statements) is projected here from the on-disk run.json, so a run's exact SQL can be read,
/// searched, and diffed from the database instead of the file. Drill-down under a <see cref="CatalogRun"/>; this
/// is the V3 equivalent of the legacy flw.SysLog.TraceLog.
/// </summary>
public class CatalogRunStatement
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public Guid? RepoId { get; set; }

    /// <summary>1-based position in the run's execution order (across every source, in projection order).</summary>
    public int Ordinal { get; set; }

    /// <summary>The run step that produced the statement (for example staging.create, target.evolve, schema.ddl,
    /// surrogateKey).</summary>
    public string Step { get; set; } = string.Empty;

    public string Sql { get; set; } = string.Empty;
}

/// <summary>One surrogate-key generation outcome of a run (ingestion flows): the IDENTITY-backed lookup-table
/// assignment, log-only (a failure never rolled back the load). Drill-down under a <see cref="CatalogRun"/>; the
/// statements it ran are in <see cref="CatalogRunStatement"/>.</summary>
public class CatalogRunSurrogateKey
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public Guid? RepoId { get; set; }

    public int SurrogateKeyId { get; set; }

    public string SurrogateTable { get; set; } = string.Empty;

    public string SurrogateColumn { get; set; } = string.Empty;

    /// <summary>True when the surrogate was generated on a different server than the flow's target.</summary>
    public bool IsRemote { get; set; }

    /// <summary>New distinct business keys inserted into the lookup table.</summary>
    public long KeysGenerated { get; set; }

    /// <summary>Base/target rows stamped with a surrogate value.</summary>
    public long RowsStamped { get; set; }

    /// <summary>True when the spec ran without an error.</summary>
    public bool Executed { get; set; }

    public string? Error { get; set; }
}

/// <summary>One per-metric health-check summary of a run (hc flows): the anomaly and model-provenance counts, so
/// a GUI can show data-quality dashboards straight from the database (the full scored series stays in the on-disk
/// healthcheck.json report). Drill-down under a <see cref="CatalogRun"/>.</summary>
public class CatalogRunHealthCheckMetric
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public Guid? RepoId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Dates in the scored series (observed plus imputed).</summary>
    public int SeriesPoints { get; set; }

    /// <summary>Dates absent from the source whose value was imputed.</summary>
    public int ImputedPoints { get; set; }

    /// <summary>Trailing points excluded from anomaly counting (data may still be arriving).</summary>
    public int ImmaturePoints { get; set; }

    public int Anomalies { get; set; }

    /// <summary>Regime changes PELT reported in this metric's residuals.</summary>
    public int LevelShifts { get; set; }

    /// <summary>True when this run trained the metric's model; false when it scored with the stored one.</summary>
    public bool ModelTrained { get; set; }

    public string? ModelTrainer { get; set; }

    /// <summary>Why the metric could not be scored, when it failed; null when it scored.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One column of a catalog object, captured by the derived (connected) lineage tier from the live database, so the
/// catalog is a cross-repo data dictionary: a GUI answers "every table/view with a column named X" across all
/// repos. Keyed to its global <see cref="CatalogObject"/> by <see cref="ObjectKey"/> (a soft link, no FK, since
/// objects are upserted globally). Only populated by a <c>--connect</c> sync.
/// </summary>
public class CatalogObjectColumn
{
    public long Id { get; set; }

    /// <summary>The owning object's global key (server reference + database + schema + name).</summary>
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>1-based column position in the object.</summary>
    public int Ordinal { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The SQL Server type rendered with length/precision (for example <c>nvarchar(100)</c>, <c>decimal(18,2)</c>).</summary>
    public string? DataType { get; set; }

    public bool Nullable { get; set; }
}

/// <summary>
/// A git repository the control plane keeps the catalog synced from: it periodically pulls the branch HEAD and
/// runs the catalog sync, so the shadow catalog stays current with git without anyone running <c>sqlflow db sync</c>
/// by hand (git stays the source of truth; this is the managed shadow). The credential to pull a private remote is
/// resolved from the control plane's own environment, never stored here. <see cref="NextSyncUtc"/> is advanced
/// atomically when a sync is claimed, so several control-plane nodes never sync the same source at once.
/// </summary>
public class CatalogRepoSource
{
    public Guid Id { get; set; }

    /// <summary>The repo's name in the catalog (the synced pipelines/runs are attributed to it). Unique.</summary>
    public string Name { get; set; } = string.Empty;

    public string RemoteUrl { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";

    /// <summary>Whether the control plane auto-syncs this source. A disabled source is kept but never pulled.</summary>
    public bool Enabled { get; set; } = true;

    public int SyncIntervalSeconds { get; set; } = 300;

    /// <summary>When the source is next due to sync (UTC); the sync loop claims it by advancing this atomically.</summary>
    public DateTime? NextSyncUtc { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    /// <summary>The commit the last successful sync pulled, so a synced estate is attributable to an exact commit.</summary>
    public string? LastSyncedSha { get; set; }

    /// <summary>The last sync's error (secret-redacted), or null when the last sync succeeded.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// One compute node in the fleet: a worker (the control-plane in-process worker, or a standalone <c>sqlflow
/// worker</c>) that drains the run queue. A node upserts a heartbeat as it polls, so the control plane and GUI can
/// see which nodes are alive and when each was last heard from. Identity is the node's name (its machine name), so
/// a restarted node re-registers in place rather than duplicating. Liveness is derived (last-seen within a recent
/// window), not stored, so it is always current at read time.
/// </summary>
public class CatalogNode
{
    /// <summary>The node's name (its machine name); the stable identity a heartbeat upserts on.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    /// <summary>The SQLFlow build the node is running, for spotting version skew across the fleet; null if unknown.</summary>
    public string? Version { get; set; }
}

/// <summary>
/// One schedule that fires a pipeline on a cron expression or a fixed interval by enqueuing a run onto the durable
/// queue (the same path a manual trigger takes). A schedule is either declared in the flow YAML and synced from git
/// (<see cref="Source"/> = <c>yaml</c>, the version-controlled source of truth) or created through the control-plane
/// API (<c>api</c>, ad-hoc). <see cref="Paused"/> is an operational override applied through the API that survives a
/// git re-sync, so pausing a git schedule from the GUI is not undone the next time the estate syncs. The scheduler
/// fires a schedule when it is <see cref="Enabled"/>, not <see cref="Paused"/>, and <see cref="NextFireUtc"/> has
/// arrived; firing advances <see cref="NextFireUtc"/> to the next future occurrence (missed occurrences are not
/// backfilled).
/// </summary>
public class CatalogSchedule
{
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The pipeline this schedule fires (its stable, repo-scoped id); a soft link, no FK.</summary>
    public Guid PipelineId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>A standard cron expression (5 fields, or 6 with seconds), evaluated in <see cref="Timezone"/>. Null
    /// when the schedule is interval-based.</summary>
    public string? Cron { get; set; }

    /// <summary>A fixed interval in seconds between fires. Null when the schedule is cron-based. Exactly one of
    /// <see cref="Cron"/> / <see cref="IntervalSeconds"/> is set.</summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>The IANA time zone the cron expression is evaluated in (for example <c>Europe/Oslo</c>); <c>UTC</c>
    /// by default. Ignored for interval schedules.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>Whether the schedule is active per its definition (the YAML <c>enabled</c> flag or the API create).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>An API-applied operational pause that is independent of <see cref="Enabled"/>, so a git re-sync of a
    /// <c>yaml</c> schedule does not clear a pause an operator set through the GUI.</summary>
    public bool Paused { get; set; }

    /// <summary><c>yaml</c> (declared in git, overwritten on sync) or <c>api</c> (created through the control plane).</summary>
    public string Source { get; set; } = "api";

    /// <summary>When the schedule next fires (UTC). The scheduler claims a schedule by advancing this atomically.</summary>
    public DateTime? NextFireUtc { get; set; }

    public DateTime? LastFireUtc { get; set; }

    /// <summary>The run id the most recent fire enqueued, for tracing a scheduled run back to its schedule.</summary>
    public Guid? LastRunId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
