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

/// <summary>The provenance of a <see cref="CatalogPipelineColumn"/>, stored as a short lowercase string (same
/// convention as <see cref="RunStatuses"/>) so the value is self-describing and filterable with plain equality.</summary>
public static class PipelineColumnKinds
{
    /// <summary>Authored in the flow YAML (the source of truth), projected on every pipeline sync.</summary>
    public const string Declared = "declared";

    /// <summary>Inferred by a run from the loaded raw data (a type-inference report), projected per run.</summary>
    public const string Detected = "detected";
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
    /// <summary>The batch label a flow reports under when its YAML declares no <c>batch</c>: every run belongs to
    /// a batch (a source executes jointly), so surfaces that group by batch coalesce a missing label to this
    /// value rather than leaving an ungroupable hole. Applied at query time; the row itself keeps the honest
    /// null so the catalog reflects exactly what the YAML says.</summary>
    public const string DefaultBatch = "default";

    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The flow kind: file / ing / exp / sp / inv / hc / scm / batch.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The batch (source system) the flow's YAML declares, the label its runs group under; null when the
    /// document declares none (grouping surfaces then fall back to <see cref="DefaultBatch"/>).</summary>
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

    /// <summary>Per-run substitution: ignore the watermark and read everything the definition selects (the
    /// built-in backfill's force-full). Recorded on the run, so every backfill is auditable from the history.</summary>
    public bool FullLoad { get; set; }

    /// <summary>Per-run substitution: the externally-bounded window's low bound (inclusive, UTC). File flows
    /// bound file dates; ingestion flows bound the incremental date column; exports re-window their chunk plan.</summary>
    public DateTime? BackfillFrom { get; set; }

    /// <summary>Per-run substitution: the window's high bound (UTC); null leaves the definition's own upper
    /// bound in effect.</summary>
    public DateTime? BackfillTo { get; set; }

    /// <summary>Per-run substitution: a glob narrowing which files a file flow reads this run.</summary>
    public string? FilePattern { get; set; }

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

    /// <summary>The generating DDL the engine emitted for this object (the <c>CREATE TABLE</c> or
    /// <c>CREATE OR ALTER VIEW</c>), captured from the run trace or a declared hook so the catalog holds the
    /// object's script even offline. Distinct from <see cref="Definition"/>: that is the live module body read
    /// from the database, this is the script we ran. Null for a pre-existing source table no tier saw created.</summary>
    public string? Script { get; set; }

    /// <summary>Which tier supplied <see cref="Script"/>: Declared / Observed / Derived. Null when there is no
    /// script.</summary>
    public string? ScriptTier { get; set; }

    /// <summary>When <see cref="Script"/> was last refreshed. Null when there is no script.</summary>
    public DateTime? ScriptUpdatedUtc { get; set; }

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

    /// <summary>Which tier supplied this column set: <c>Derived</c> (read live from the database),
    /// <c>Observed</c> (parsed from the CREATE TABLE the run executed), or <c>Declared</c>. The sync keeps a
    /// live (derived) dictionary from being overwritten by an offline (observed) one.</summary>
    public string Tier { get; set; } = string.Empty;
}

/// <summary>
/// One resolved column of a pipeline's pre-ingestion transformation view: the modernized, central form of a
/// legacy <c>flw.PreIngestionTransform</c> row, so the estate can be queried for "which transformations are set
/// or detected on a pipeline". Two provenances share the row shape, distinguished by <see cref="Kind"/>:
/// <c>declared</c> rows are projected from the flow YAML (the source of truth, refreshed on every pipeline sync),
/// and <c>detected</c> rows are projected from a type-inference report produced by a run against the loaded raw
/// data. Repo-scoped and keyed to its pipeline by <see cref="PipelineId"/> (a soft link, no FK, matching the rest
/// of the catalog). Replaced by (pipeline, kind) so a re-sync or a fresh run reflects the current transforms.
/// </summary>
public class CatalogPipelineColumn
{
    public long Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The owning pipeline's stable id (FlowIdentity of the flow name); a soft link (no FK).</summary>
    public Guid PipelineId { get; set; }

    /// <summary><c>declared</c> (authored in YAML) or <c>detected</c> (inferred by a run from the raw data).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>1-based position of the column in the resolved transformation view.</summary>
    public int Ordinal { get; set; }

    /// <summary>The output column name in the view (the alias when the transform renames, else the source name).</summary>
    public string ColumnName { get; set; } = string.Empty;

    /// <summary>The raw source column the transform reads (legacy ColumnName); null for a virtual/computed column.</summary>
    public string? SourceColumn { get; set; }

    /// <summary>The SQL expression producing the value, with <c>@ColName</c> already resolved to the source column
    /// reference; null when the column is a straight pass-through with no transform.</summary>
    public string? Expression { get; set; }

    /// <summary>The column's SQL type (declared in YAML, or inferred); null when a plain expression's type is not
    /// declared.</summary>
    public string? DataType { get; set; }

    /// <summary>The authored sort order (legacy ColumnSortOrder); null when the column keeps its natural position.</summary>
    public int? SortOrder { get; set; }

    /// <summary>A computed column with no raw source counterpart (legacy Virtual indicator).</summary>
    public bool IsVirtual { get; set; }

    /// <summary>Computed but dropped from the view's final projection (legacy ExcludeFromView).</summary>
    public bool ExcludeFromView { get; set; }

    /// <summary>True when a real conversion/expression was applied (as opposed to a raw string pass-through).</summary>
    public bool Converted { get; set; }
}

/// <summary>
/// A git repository the control plane keeps the catalog synced from: it periodically pulls the branch HEAD and
/// runs the catalog sync, so the shadow catalog stays current with git without anyone running <c>sqlflow db sync</c>
/// by hand (git stays the source of truth; this is the managed shadow). The credential to pull a private remote is
/// named by <see cref="CredentialReference"/> (a <c>${keyvault:...}</c>/<c>${env:...}</c> reference resolved at
/// clone time); the secret value itself is created and maintained in the vault, never stored here.
/// <see cref="NextSyncUtc"/> is advanced atomically when a sync is claimed, so several control-plane nodes never
/// sync the same source at once.
/// </summary>
public class CatalogRepoSource
{
    public Guid Id { get; set; }

    /// <summary>The repo's name in the catalog (the synced pipelines/runs are attributed to it). Unique.</summary>
    public string Name { get; set; } = string.Empty;

    public string RemoteUrl { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";

    /// <summary>A secret reference (<c>${keyvault:vault/secret}</c> or <c>${env:NAME}</c>) for the token used to
    /// pull a private remote, resolved through the SqlFlow secret resolver at clone time. Only the reference is
    /// stored; the secret value lives in the vault and is created/maintained there. Null falls back to the host's
    /// own environment (<c>SQLFLOW_GIT_TOKEN</c>), which covers public remotes.</summary>
    public string? CredentialReference { get; set; }

    /// <summary>The git username paired with the resolved token. Optional and not a secret; required by hosts that
    /// authenticate the username too (Bitbucket app passwords). Null uses the token-only placeholder GitHub accepts.</summary>
    public string? CredentialUsername { get; set; }

    /// <summary>The flow files this source deliberately does NOT import, from a preview-first scan: a JSON array of
    /// repo-relative, forward-slashed paths. Null or empty imports every <c>*.flow.yaml</c> (the default, and
    /// backward-compatible). Applied on every sync, so a previously-imported flow that becomes excluded is
    /// deactivated on the next sync (its run history is kept), and a newly-included flow is imported then. The
    /// selection lives here, but nothing reaches the catalog until a sync runs, which is what the scheduler reads.</summary>
    public string? ExcludedFlowPaths { get; set; }

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

/// <summary>The identity providers a <see cref="CatalogUser"/> can come from, stored as a short lowercase string
/// (same convention as <see cref="RunStatuses"/>).</summary>
public static class UserProviders
{
    /// <summary>A regular SQLFlow user: username + password hash held in the catalog.</summary>
    public const string Local = "local";

    /// <summary>A Microsoft Entra ID user provisioned just-in-time from a validated Entra token.</summary>
    public const string Entra = "entra";
}

/// <summary>The built-in role names. Roles live in <see cref="CatalogRole"/> rows (seeded at bootstrap) so their
/// scope grants are visible and queryable in the database; these constants exist so code never scatters string
/// literals.</summary>
public static class RoleNames
{
    /// <summary>Full control: read, operate, and user/role administration.</summary>
    public const string Admin = "admin";

    /// <summary>Day-to-day operations: read everything, trigger/cancel runs, manage schedules and repo sources.</summary>
    public const string Operator = "operator";

    /// <summary>Read-only access to the whole API surface.</summary>
    public const string Viewer = "viewer";

    /// <summary>Every built-in role, for validation messages and seeding.</summary>
    public static readonly string[] All = [Admin, Operator, Viewer];
}

/// <summary>
/// One role: a named set of API scopes. The built-in roles (admin / operator / viewer) are seeded by the control
/// plane's bootstrap provisioning; a user's effective scopes at login are read from their role's row, so a grant
/// change takes effect on the next token without a redeploy.
/// </summary>
public class CatalogRole
{
    /// <summary>The role name (the stable identity users reference).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The scopes the role grants, space-delimited exactly as they appear in the token's <c>scope</c>
    /// claim (for example <c>read operate admin</c>).</summary>
    public string Scopes { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One user of the control plane. Two kinds share the row shape: a regular SQLFlow user
/// (<see cref="UserProviders.Local"/>, authenticated by <see cref="PasswordHash"/>) and a Microsoft Entra ID user
/// (<see cref="UserProviders.Entra"/>, authenticated by Entra and provisioned just-in-time on first sign-in, keyed
/// by <see cref="ExternalObjectId"/>). Either way the control plane issues its own token; this row supplies the
/// subject and the role the token's scopes come from. Users are deactivated rather than deleted so audit history
/// stays attributable.
/// </summary>
public class CatalogUser
{
    public Guid Id { get; set; }

    /// <summary>The sign-in name (for an Entra user, the account's UPN/email). Unique across providers.</summary>
    public string Username { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>The PBKDF2 password hash for a local user; null for an SSO user (they have no local password).</summary>
    public string? PasswordHash { get; set; }

    /// <summary>The role granting this user's scopes; references <see cref="CatalogRole.Name"/> (soft link).</summary>
    public string Role { get; set; } = RoleNames.Viewer;

    /// <summary>Where the user authenticates: <see cref="UserProviders.Local"/> or <see cref="UserProviders.Entra"/>.</summary>
    public string Provider { get; set; } = UserProviders.Local;

    /// <summary>The Entra object id (<c>oid</c> claim) for an SSO user: the immutable identity JIT provisioning
    /// keys on, so a UPN rename never creates a duplicate. Null for local users.</summary>
    public string? ExternalObjectId { get; set; }

    /// <summary>An inactive user cannot sign in (local) or exchange a token (SSO). Deactivation is the delete.</summary>
    public bool Active { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }
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

    /// <summary>Whether missed occurrences are backfilled. False (the default) skips a fire the host missed and
    /// resumes at the next occurrence after now; true catches up, firing one missed occurrence per scheduler tick
    /// until current. Applied by the scheduler when it advances <see cref="NextFireUtc"/>.</summary>
    public bool Catchup { get; set; }

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
