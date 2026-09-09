using SqlFlow.Core.Runs;

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

    /// <summary>A group member that was never run because a flow it depends on (transitively) failed or was
    /// cancelled earlier in the same run group. Terminal and unsuccessful, but distinct from <c>failed</c>: this
    /// run did not itself execute or error, it was passed over so a broken upstream is not fed downstream.</summary>
    public const string Skipped = "skipped";

    /// <summary>The terminal states; a run in one of these is finished and will not change.</summary>
    public static bool IsTerminal(string status)
        => status is Succeeded or Failed or Cancelled or Skipped;
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
/// <c>DefinitionJson</c> (the parsed flow, normalized to JSON) so any field is queryable with SQL Server's
/// JSON functions across every file and repo, and in <c>Yaml</c> (the original text) for display. Both are
/// secret-redacted on the way in. <c>Id</c> is the flow's stable identity (the deterministic GUID the
/// engine derives from the name), so runs join to it with no run-time database round-trip.
/// </summary>
/// <summary>The catalog spellings of a flow's execution mode (the YAML <c>mode:</c>), stored as a string so the
/// column reads plainly in SQL. Mapped from <c>SqlFlow.Core.Runs.ExecutionMode</c> at sync time.</summary>
public static class PipelineExecutionModes
{
    public const string Auto = "auto";

    public const string Manual = "manual";

    /// <summary>Deactivated: a retired pipeline that automatic execution (schedule fires, node/batch group
    /// expansion) must never pick up; still runnable by a direct trigger.</summary>
    public const string Disabled = "disabled";

    /// <summary>The stored spelling of a core execution mode.</summary>
    public static string From(Core.Runs.ExecutionMode mode) => mode switch
    {
        Core.Runs.ExecutionMode.Manual => Manual,
        Core.Runs.ExecutionMode.Disabled => Disabled,
        _ => Auto,
    };
}

/// <summary>The catalog spellings of a flow's lifecycle (the YAML <c>lifecycle:</c>), stored as a string so the
/// column reads plainly in SQL. Mapped from <c>SqlFlow.Core.Runs.FlowLifecycle</c> at sync time.</summary>
public static class PipelineLifecycles
{
    public const string Production = "production";

    public const string Development = "development";

    /// <summary>The stored spelling of a core lifecycle.</summary>
    public static string From(Core.Runs.FlowLifecycle lifecycle)
        => lifecycle == Core.Runs.FlowLifecycle.Development ? Development : Production;
}

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

    /// <summary>How the flow executes (see <see cref="PipelineExecutionModes"/>): <c>auto</c> (the default)
    /// participates in schedules and batch/node group runs; <c>manual</c> (a health-check flow's
    /// <c>mode: manual</c>) is excluded from every automatic dispatch and runs only when triggered directly.</summary>
    public string ExecutionMode { get; set; } = PipelineExecutionModes.Auto;

    /// <summary>The flow's declared lifecycle (see <see cref="PipelineLifecycles"/>): <c>production</c> (the
    /// default) generates notification events on failure; <c>development</c> runs identically but never alerts,
    /// so a pipeline being built cannot page its subscribers.</summary>
    public string Lifecycle { get; set; } = PipelineLifecycles.Production;

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
    /// run). The basis for crash recovery: a run left <c>running</c> by a node that died is requeued by the
    /// control plane's orphan reaper (or failed once <see cref="Attempt"/> reaches the retry cap), so losing a
    /// worker mid-run is recoverable rather than terminal.</summary>
    public string? ClaimedByNode { get; set; }

    /// <summary>How many times this run has been claimed for execution, incremented atomically by the claim itself.
    /// Serves two roles at once. As a retry counter it bounds crash recovery: an orphaned <c>running</c> run is
    /// requeued only while this is under the cap, so a poison run that kills its node cannot crash-loop the fleet
    /// forever. As a fencing token it guards every outcome write: the claim returns the incremented value to the
    /// claiming node, and the node's completion/failure/cancel writes are conditional on the row still carrying
    /// that exact value, so a zombie node (presumed dead, actually alive) can never overwrite the outcome of a
    /// requeued and re-claimed execution with its own stale result.</summary>
    public int Attempt { get; set; }

    /// <summary>When an operator asked to cancel this run while it was already <c>running</c> (a queued run is
    /// cancelled outright, so this stays null for that path). It is a durable request, not the outcome: the owning
    /// node observes it on its next poll, trips the run's cancellation token to abort the in-flight statement, and
    /// records the run <c>cancelled</c>. Null means no cancel was requested.</summary>
    public DateTime? CancelRequestedUtc { get; set; }

    /// <summary>The pool this run is routed to: only a worker that serves this pool may claim it. Null means
    /// "any node" (untargeted) - any worker claims it. Routing keeps a flow on a node that can actually reach its
    /// database and resolve its secrets (least privilege), so an on-prem flow runs on an on-prem node and a cloud
    /// flow on a cloud node.</summary>
    public string? TargetPool { get; set; }

    /// <summary>The git commit the run should execute, when it is pinned to one: the node materializes the repo at
    /// this exact SHA and runs the flow from there, so the run is reproducible and a node can run a flow it has no
    /// local copy of. Null runs the flow from the node's locally synced repo path (the default).</summary>
    public string? CommitSha { get; set; }

    /// <summary>The content hash of the exact YAML this run executes, snapshotted from the pipeline row at enqueue
    /// time into <see cref="CatalogFlowVersion"/>. This is the primary delivery path: the executing node loads the
    /// YAML from the catalog by this hash instead of cloning the repo, so a schedule fanning out a whole batch
    /// never storms the git remote. Null when no snapshot could be taken at enqueue (the pipeline row was missing
    /// or empty); the node then falls back to git materialization via <see cref="CommitSha"/>.</summary>
    public string? FlowVersionHash { get; set; }

    /// <summary>The operation the run performs (<c>RunParameters.Operations</c>: deliver, verify, plan, known-state).
    /// Recorded on the run so the history says what was asked, and indexed so a listing can filter on it.</summary>
    public string Operation { get; set; } = "deliver";

    /// <summary>Per-run substitution: force past the change gates (plan every record, re-plan a completed submission,
    /// verify recently verified records). Recorded on the run so a forced run is distinguishable in the history.</summary>
    public bool Force { get; set; }

    /// <summary>The submission the run re-runs or was scoped to (<c>RunParameters.SubmissionId</c>), or null. Indexed,
    /// so a submission's page lists every run that touched it.</summary>
    public Guid? SubmissionId { get; set; }

    /// <summary>The full <c>RunParameters</c> the run was triggered with, as JSON (parameter values, an explicit drop, the
    /// record scope, the publication target), or null for the defaults. The authoritative record of what was asked;
    /// the node reads the run's parameters back from here.</summary>
    public string? ParametersJson { get; set; }

    /// <summary>The run group this run belongs to when it was enqueued as one member of a multi-flow execution
    /// (a Node run: a flow and its descendants; or a Batch run: a whole data source), or null for a standalone
    /// single-flow run. Members of a group share this id and are ordered by <see cref="GroupWave"/>: the queue
    /// claim only makes a member claimable once every same-group member in a lower wave is terminal, so a
    /// dependency never runs before what it depends on.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>This member's execution wave within its <see cref="GroupId"/> (the flow's topological level from
    /// lineage). Lower waves run first; members in the same wave run concurrently. Ignored for a standalone run
    /// (<see cref="GroupId"/> null), where it stays 0.</summary>
    public int GroupWave { get; set; }

    /// <summary>
    /// The cap on how many members of this run's <see cref="GroupId"/> may execute at once, or null for unbounded.
    /// Copied from the firing schedule's <see cref="CatalogSchedule.MaxConcurrency"/> at enqueue rather than joined
    /// at claim time, for two reasons: the claim predicate stays a single-table read on the queue's hot path, and a
    /// group already in flight keeps the bound it was queued under, so editing the schedule never retunes a wave
    /// that is already running. Null on a standalone run, which has no group to bound.
    /// </summary>
    public int? GroupMaxConcurrency { get; set; }

    /// <summary>
    /// What asked for this run (see <see cref="RunTriggerSources"/>): a schedule firing, a person or API call,
    /// or a local CLI execution synced in from its artifact. Null on every run recorded before this column
    /// existed, which is the only reason a reader must treat it as unknown rather than as "not a schedule".
    /// <para>
    /// It exists because nothing else on the row answers the question. A schedule fire and someone clicking
    /// Run in the GUI both go through the same enqueue and produce byte-identical rows, so monitoring that
    /// wants to judge a stream against its declared cadence could previously only approximate it by asking
    /// whether the flow is a member of a schedule at all.
    /// </para>
    /// </summary>
    public string? TriggerSource { get; set; }

    /// <summary>The schedule whose fire enqueued this run, when <see cref="TriggerSource"/> is
    /// <see cref="RunTriggerSources.Schedule"/>; null otherwise. A soft link (no FK), so a run outlives the
    /// schedule being renamed out of the catalog by a later sync.</summary>
    public Guid? TriggerScheduleId { get; set; }

    /// <summary>Who asked for the run when a person or a client did (the signed-in user of a trigger call, the
    /// user who fired a schedule by hand); null for the scheduler's own fires and for artifacts synced in from
    /// disk. Recorded so the history and the delivery ledger's audit trail name the actor.</summary>
    public string? RequestedBy { get; set; }

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

    /// <summary>The submission the run produced or completed, projected from <c>result.submissionId</c>; null for a
    /// verify, plan or known-state run. Distinct from the requested <see cref="SubmissionId"/> above: a deliver run
    /// registers the drop's submission here.</summary>
    public Guid? ResultSubmissionId { get; set; }

    /// <summary>Records the run planned for delivery (<c>result.planned</c>), or checked (a verify run).</summary>
    public int? RecordsPlanned { get; set; }

    /// <summary>Records delivered by the run (<c>result.delivered</c>), or matched (a verify run).</summary>
    public int? RecordsDelivered { get; set; }

    /// <summary>Records the run left held (<c>result.held</c>), or found drifted or missing (a verify run).</summary>
    public int? RecordsHeld { get; set; }

    /// <summary>Records that failed in the run (<c>result.failed</c>), or errored (a verify run).</summary>
    public int? RecordsFailed { get; set; }

    /// <summary>Records skipped as unchanged (<c>result.skippedUnchanged</c>).</summary>
    public int? RecordsSkipped { get; set; }

    /// <summary>The artifact's <c>result</c> object as JSON: the kind-specific outcome (a deliver run's counts and
    /// submission, a fan-out member's partition counts), for the run page and for the run that fanned it out.</summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// For a fan-out member (an intake partition or a drain a deliver run spread across the fleet): the run that
    /// fanned it out. Members run concurrently with their root and with each other on one pipeline; the claim gate
    /// serializes only runs of different families. Null for every other run.
    /// </summary>
    public Guid? FanOutRoot { get; set; }

    /// <summary>The member's slot (1-based) within its fan-out, for display.</summary>
    public int? FanOutSlot { get; set; }

    /// <summary>How many members the fan-out has, for display.</summary>
    public int? FanOutCount { get; set; }
}

/// <summary>
/// What asked for a run, stored as a short lowercase string (same convention as <see cref="RunStatuses"/>).
/// Recorded at enqueue, so it says what actually happened rather than what can be inferred afterwards.
/// </summary>
public static class RunTriggerSources
{
    /// <summary>A schedule fired and enqueued it (<see cref="CatalogRun.TriggerScheduleId"/> names which).</summary>
    public const string Schedule = "schedule";

    /// <summary>A person or an API client asked for it: the GUI's Run button, a run trigger call, an MCP
    /// tool. Deliberately one value and not several, because the distinction that matters to a reader is
    /// "the estate ran this on its own" versus "somebody asked for it".</summary>
    public const string Manual = "manual";

    /// <summary>Recorded from an on-disk artifact rather than enqueued: a local <c>sqlflow run</c> synced in.</summary>
    public const string Cli = "cli";

    /// <summary>Every source, in display order.</summary>
    public static readonly IReadOnlyList<string> All = [Schedule, Manual, Cli];
}

/// <summary>The modes a run group can be launched in, stored as a short lowercase string (same convention as
/// <see cref="RunStatuses"/>).</summary>
public static class RunGroupModes
{
    /// <summary>A flow and all of its transitive descendants (the legacy "Node" execution), run in wave order.</summary>
    public const string Node = "node";

    /// <summary>Every active flow in one batch / data source (the legacy "Batch" execution), run in wave order.</summary>
    public const string Batch = "batch";

    /// <summary>The members a deliver run fanned its intake or its drains out to, all in one wave.</summary>
    public const string FanOut = "fan-out";
}

/// <summary>
/// The header of one multi-flow execution: a Node run (a flow plus its descendants) or a Batch run (a whole data
/// source), enqueued as a set of <see cref="CatalogRun"/> members that share this row's <see cref="GroupId"/>. The
/// members carry the ordering (<see cref="CatalogRun.GroupWave"/>) and the live per-flow state; this row is the
/// durable header the GUI shows and cancels as a unit, and the record of what was asked for (mode, anchor, size).
/// </summary>
public class CatalogRunGroup
{
    public Guid GroupId { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The group mode (see <see cref="RunGroupModes"/>): whether the set was derived from a flow's
    /// descendants (<c>node</c>) or a whole batch (<c>batch</c>).</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>What the set was expanded from: the anchor flow name for a Node run, the batch label for a Batch
    /// run. Kept for display and audit ("Node run of Orders", "Batch run of Baatbooking").</summary>
    public string Anchor { get; set; } = string.Empty;

    /// <summary>How many flow members were enqueued in this group.</summary>
    public int MemberCount { get; set; }

    /// <summary>The git commit every member was pinned to at enqueue time (as resolved by the queue), or null when
    /// the members run unpinned from the node's local copy.</summary>
    public string? CommitSha { get; set; }

    public DateTime EnqueuedUtc { get; set; }
}

/// <summary>
/// One executable YAML version, content-addressed by its hash: the exact document text a run executes, snapshotted
/// from the pipeline row when the run is enqueued. This is how flow content reaches a compute node: the enqueue
/// stamps <see cref="CatalogRun.FlowVersionHash"/> and inserts this row if the version is new, and the node loads
/// the YAML from here instead of materializing the repo from its git remote, so a schedule fanning out a whole
/// batch costs zero clones. Rows are immutable (same hash = same bytes) and deduplicated: a hundred runs at one
/// commit share one row. The text is the same secret-redacted form the pipeline row stores; redaction is a no-op
/// for compliant (reference-only) flows, and a flow that embeds a literal credential is never snapshotted (the
/// enqueue leaves the hash null and the node keeps the git path for it).
/// </summary>
public class CatalogFlowVersion
{
    /// <summary>SHA-256 (hex) of <see cref="Yaml"/>: the content address, and the value runs reference through
    /// <see cref="CatalogRun.FlowVersionHash"/>.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The full YAML document text this version executes (secret-redacted, like the pipeline row's copy;
    /// identical to the committed bytes for reference-only flows).</summary>
    public string Yaml { get; set; } = string.Empty;

    /// <summary>When this version was first snapshotted (the first enqueue that referenced it).</summary>
    public DateTime FirstSeenUtc { get; set; }
}

/// <summary>
/// One canonical run event: a progress, decision, or warning event the engine published while the run executed
/// (a file it started reading, the watermark it resolved, a stage summary with rows and timing, an engine
/// decision, a warning). Projected from the run.json <c>events</c> array at completion; while a run is live the
/// node streams these rows in as the events happen, so the run detail's Events view updates in flight. Generated
/// SQL is deliberately not duplicated here: statements live in the generated-statement trace and the two
/// streams are interleaved by timestamp when the timeline is shown. Drill-down under a <see cref="CatalogRun"/>.
/// </summary>
public class CatalogRunEvent
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public Guid? RepoId { get; set; }

    /// <summary>1-based position in the run's event order (publication order).</summary>
    public int Ordinal { get; set; }

    /// <summary>When the event happened (UTC).</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>The event's level: trace, debug, info, warning, or error (see RunEventLevels in SqlFlow.Core).</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>The run step or stage the event belongs to (for example source.open, incremental,
    /// target.evolve); null for flow-level events that have no stage.</summary>
    public string? Step { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Row count attached to the event (a stage summary, a file read), when the emitter measured one.</summary>
    public long? Rows { get; set; }

    /// <summary>Elapsed milliseconds attached to the event (a stage summary), when the emitter measured one.</summary>
    public double? ElapsedMs { get; set; }
}

/// <summary>Operator-tunable maintenance settings, held as a single row (<see cref="Id"/> is always 1) so the GUI's
/// Maintenance page can change them without a redeploy. Today it carries only the run-trace retention.</summary>
public class CatalogMaintenanceSetting
{
    /// <summary>The fixed singleton key (always 1); one row governs the whole estate.</summary>
    public int Id { get; set; }

    /// <summary>How many days a superseded successful run keeps its SQL trace (the generated-statement trace /
    /// <see cref="CatalogRunEvent"/>) before it is pruned; null keeps every trace forever (age-based pruning off,
    /// the default until an operator sets a value). Each pipeline's latest run and every failed run are kept
    /// regardless of this, so the current state and every failure reason always survive.</summary>
    public int? RunTraceRetentionDays { get; set; }

    /// <summary>When the settings were last changed (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who last changed the settings (the caller's username), for a light audit trail; null before any
    /// edit.</summary>
    public string? UpdatedBy { get; set; }
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

    /// <summary>Set when an operator triggers a manual "sync now": the next sync recomputes lineage in full
    /// (objects, edges, waves, and the offline object-body/column enrichment) instead of taking the cheap
    /// unchanged-estate shortcut, then clears the flag on success. The periodic auto-sync leaves it false, so
    /// it keeps skipping the recompute when nothing changed. A durable, cross-replica signal, since the API
    /// request and the sync worker can run on different control-plane nodes.</summary>
    public bool ForceLineageOnNextSync { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// One entry of a control-plane <em>activity</em> trace: an append-only log row a long-running operation writes as
/// it progresses, so the GUI can surface live insight into what is happening. It is the general-purpose twin of
/// <see cref="CatalogRunEvent"/> (which is specific to a flow run): the same append-only-log-tailed-by-a-monotonic
/// <see cref="Id"/> mechanism the run trace uses, but keyed by a free-form <see cref="Kind"/> (the operation type,
/// e.g. <c>repo-sync</c>, <c>lineage</c>) and <see cref="SubjectKey"/> (the thing it acts on, e.g. a repo source
/// id) rather than a run id, so any operation can stream a trace without inventing its own table and endpoint. A
/// repository sync writes claimed / credentials / clone / checkout / reconcile / result / warnings / outcome; a
/// lineage computation or any other operation writes its own phases the same way. The GUI's bottom trace panel
/// tails these rows for one (kind, subject). Rows are kept for a handful of recent activities per subject and
/// pruned as new ones begin.
/// </summary>
public class CatalogActivityEvent
{
    /// <summary>The append-only, monotonically increasing id the trace stream tails on (its cursor).</summary>
    public long Id { get; set; }

    /// <summary>One run of one operation (its correlation id): every event of a single sync attempt / lineage
    /// computation shares it, so the panel can group and the pruning can keep only the newest few per subject.</summary>
    public Guid ActivityId { get; set; }

    /// <summary>The operation type, so one subject's unrelated activities do not intermix (e.g. <c>repo-sync</c>,
    /// <c>lineage</c>). The (kind, subject) pair is what a panel streams.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The entity the activity acts on, as a string key (a repo source id, a repo id, a project key). Paired
    /// with <see cref="Kind"/> it identifies the trace a panel follows.</summary>
    public string SubjectKey { get; set; } = string.Empty;

    /// <summary>1-based position within the activity (emission order).</summary>
    public int Ordinal { get; set; }

    /// <summary>When the event happened (UTC).</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>The event's level: info, warning, or error (see RunEventLevels in SqlFlow.Core).</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>The phase the event belongs to (operation-defined, e.g. start, clone, sync, result, done); null for a
    /// free-standing line.</summary>
    public string? Step { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>True on the activity's final event, which carries the terminal <see cref="Status"/>. The trace stream
    /// ends once the newest event for the (kind, subject) is terminal.</summary>
    public bool Terminal { get; set; }

    /// <summary>On the terminal event, the outcome ("succeeded" or "failed"); null on every other row.</summary>
    public string? Status { get; set; }
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

    /// <summary>The pool this node serves, so the fleet view can count how many workers are online per pool (and show
    /// a pool that is still spinning one up). The empty string is the default (untargeted) pool; null is a node that
    /// registered before pools were recorded and is treated as the default pool.</summary>
    public string? Pool { get; set; }

    /// <summary>How many runs this node was executing at its last heartbeat. The autoscaler's scale-in signal: the
    /// replica target counts nodes that are busy (this &gt; 0 and recently heartbeated) alongside the queued
    /// backlog, so occupied workers hold their replicas while idle ones remain the reclaimable surplus. Refreshed
    /// on every heartbeat; a stale row is excluded by the same liveness window the orphan reaper uses, so a dead
    /// node's last busy count can never pin a replica.</summary>
    public int BusyRuns { get; set; }

    /// <summary>When set, an operator has asked this node to restart. The worker observes it on its heartbeat cadence,
    /// stops claiming, drains its in-flight work, and exits, after which the orchestrator (Container Apps / K8s)
    /// recreates the replica. A worker honors a request only if it is newer than its own process start, so a stale
    /// request left on a row never bounces the replacement (and a restart never loops); the request is not explicitly
    /// cleared because the recreated replica comes up either under a new node identity, whose row is fresh, or under
    /// the same identity but with a later start time that makes the old request inert.</summary>
    public DateTime? RestartRequestedUtc { get; set; }
}

/// <summary>
/// The desired compute state for one worker pool, written by the control plane (the GUI's fleet controls) and read
/// by the autoscaler. The pool autoscales on queue depth, but that alone cannot express "keep at least one worker
/// warm" or "bring a worker up now even though nothing is queued", so this row carries those intents: the scaler's
/// replica target is the greatest of the pool's queued-run count, <see cref="MinReplicas"/> (an always-on floor), and
/// <see cref="ManualReplicas"/> while <see cref="ManualUntilUtc"/> has not passed (a bounded manual override, so a
/// one-off spawn or scale-up reverts to pure autoscaling on its own rather than pinning the pool warm forever). The
/// control plane never talks to the orchestrator: it only writes this row, and the autoscaler (which already queries
/// the catalog) reads it, so influencing compute needs no infrastructure credentials. One row per pool; the default
/// untargeted pool is keyed by the empty string.
/// </summary>
public class CatalogWorkerPoolDesired
{
    /// <summary>The pool name this desired state governs; the empty string is the default (untargeted) pool, whose
    /// runs carry a null <c>TargetPool</c>.</summary>
    public string Pool { get; set; } = string.Empty;

    /// <summary>The always-on floor: the pool is kept at at least this many replicas regardless of queue depth, so a
    /// warm worker is always present (0, the default, restores pure autoscaling including scale-to-zero).</summary>
    public int MinReplicas { get; set; }

    /// <summary>A manual replica target that applies only while <see cref="ManualUntilUtc"/> is in the future: it
    /// forces the pool up to this size even with nothing queued (a spawn-from-zero or a temporary scale-up), then
    /// lapses back to autoscaling when the window ends.</summary>
    public int ManualReplicas { get; set; }

    /// <summary>When the <see cref="ManualReplicas"/> override stops applying; null or in the past means no override
    /// is active. Bounding it in time is deliberate, so a manual spawn cannot silently keep a pool warm indefinitely.</summary>
    public DateTime? ManualUntilUtc { get; set; }

    /// <summary>When this desired state was last changed, for the fleet view's audit line.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who last changed it (the operator's user name), for the same audit line; null if unattributed.</summary>
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// One queued ad-hoc datasource compute task: an interactive inspection (list databases/schemas/tables, search,
/// introspect an object, test connectivity, detect a unique key) requested through the control plane and executed
/// by whichever worker node can reach the source. The row IS the queue entry, the live status, and the durable
/// result, mirroring how <see cref="CatalogRun"/> works for flow runs: references only travel through it (the
/// node resolves credentials from its own environment), the claim is atomic across concurrent workers, and the
/// result/error is recorded on the same row the trigger returned, so <c>GET /datasources/tasks/{id}</c> reflects
/// the task from queued to terminal. Lifecycle states reuse <see cref="RunStatuses"/>.
/// </summary>
public class CatalogComputeTask
{
    public Guid TaskId { get; set; }

    /// <summary>The operation (one of the closed ComputeOperations set, e.g. <c>listObjects</c>).</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>The connection reference the task runs against: a whole <c>${...}</c> reference or an
    /// <c>@alias</c>, never an inline connection string and never a secret.</summary>
    public string SourceRef { get; set; } = string.Empty;

    /// <summary>The provider kind for a <c>${...}</c> reference (MSSQL / AZDB / MySQL / PostgreSQL / Oracle);
    /// null defaults to SQL Server, and an <c>@alias</c> takes its kind from the registry regardless.</summary>
    public string? ProviderKind { get; set; }

    /// <summary>The full validated payload (operation arguments) as compact JSON: the single contract the
    /// worker deserializes and executes, so the queue row is self-contained.</summary>
    public string ArgumentsJson { get; set; } = string.Empty;

    /// <summary>The pool this task is routed to (a node that can reach the source); null means any node.</summary>
    public string? TargetPool { get; set; }

    /// <summary>The task's lifecycle state (see <see cref="RunStatuses"/>).</summary>
    public string Status { get; set; } = RunStatuses.Queued;

    /// <summary>Who asked (the token subject), recorded so ad-hoc compute against live sources is auditable.</summary>
    public string? RequestedBy { get; set; }

    public DateTime EnqueuedUtc { get; set; }

    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    /// <summary>The node that claimed the task; the basis for orphan recovery after a node restart.</summary>
    public string? ClaimedByNode { get; set; }

    /// <summary>When an operator asked to cancel the task while it was running (a queued task cancels
    /// outright); the owning node observes it on its next poll and aborts the in-flight query.</summary>
    public DateTime? CancelRequestedUtc { get; set; }

    /// <summary>The failure message (secret-redacted); null unless the task failed.</summary>
    public string? Error { get; set; }

    /// <summary>The operation's result as JSON (shape depends on the operation); null until succeeded.</summary>
    public string? ResultJson { get; set; }
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
/// One personal access token: a long-lived, per-user bearer credential for headless clients (the VSCode extension,
/// the CLI, scripted automation) that cannot sit in the browser's short session. The secret is shown once at
/// creation and never stored; only its <see cref="TokenHash"/> (SHA-256 of the secret) is kept, so a leaked
/// database row cannot be replayed as a credential. <see cref="Scopes"/> is the cap chosen at creation (always a
/// subset of the owner's own scopes); the effective grant at authentication time is this cap intersected with the
/// owner's current role scopes, so demoting or deactivating the user shrinks or kills every token they hold without
/// touching the token rows. A token is revoked (<see cref="RevokedUtc"/> set) rather than deleted so its last-used
/// history stays attributable; expiry (<see cref="ExpiresUtc"/>) is optional.
/// </summary>
public class CatalogAccessToken
{
    public Guid Id { get; set; }

    /// <summary>The owning user (<see cref="CatalogUser.Id"/>); a soft link, matching the rest of the catalog.</summary>
    public Guid UserId { get; set; }

    /// <summary>A human label the owner gives the token so they can tell their tokens apart (for example
    /// "vscode-laptop"). Not unique.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The lowercase-hex SHA-256 of the full secret: what a presented token is hashed to and looked up by.
    /// The secret itself is high-entropy (256 bits), so a plain unsalted hash is both safe and required for the
    /// O(1) lookup; no per-token salt or slow KDF is needed or wanted here.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The token's leading, non-secret characters (for example <c>sqlf_a1b2c3</c>), stored for display so
    /// the owner can recognize a token in the list without the (unrecoverable) full secret.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>The scopes granted at creation, space-delimited exactly as they appear in a token's <c>scope</c>
    /// claim. Always a subset of the creator's own scopes; further intersected with the owner's live role scopes at
    /// authentication time.</summary>
    public string Scopes { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the token stops being accepted; null for a token that never expires.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>When the token last authenticated a request; null until first use. Updated at most periodically (not
    /// on every request) so a busy token does not write on every call.</summary>
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>When the token was revoked; null while active. Revocation is the delete, so history stays.</summary>
    public DateTime? RevokedUtc { get; set; }
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

    /// <summary>
    /// The schedule's name: what a flow joins with <c>schedule: &lt;name&gt;</c>, and this row's identity within the
    /// repo. Every schedule is named (an unnamed inline block takes its declaring flow's name), so a fire always
    /// resolves to a member set. Unique per repo.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A standard cron expression (5 fields, or 6 with seconds), evaluated in <see cref="Timezone"/>. Null
    /// when the schedule is interval-based.</summary>
    public string? Cron { get; set; }

    /// <summary>A fixed interval in seconds between fires. Null when the schedule is cron-based. Exactly one of
    /// <see cref="Cron"/> / <see cref="IntervalSeconds"/> / <see cref="Parents"/> drives a schedule.</summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>
    /// The schedules this one chains behind, within the same repo; empty for a clock-driven schedule. When any exist
    /// this is a SHADOW schedule: it has no cadence, <see cref="NextFireUtc"/> stays null so the clock scan never
    /// sees it, and it fires once its parents' fires complete. One parent is a chain link; several is a FAN-IN, where
    /// the fire waits for all of them.
    /// <para>
    /// Parents are stored by NAME rather than by id because git is the source of truth and a sync rewrites rows: a
    /// name survives a parent being deleted and re-created, and it is what the YAML actually said. A named parent
    /// that does not exist simply never becomes ready, which is the same quiet stall a mis-declared cycle produces.
    /// </para>
    /// </summary>
    public ICollection<CatalogScheduleParent> Parents { get; set; } = [];

    /// <summary>
    /// How recently every parent must have fired for the fire to count as fed by current data, in hours; 0 disables
    /// the check. Staleness never blocks: it is recorded on <see cref="LastStaleParents"/> and logged. See
    /// <c>ScheduleSpec.ParentFreshnessHours</c> for why reporting beats blocking here.
    /// </summary>
    public int ParentFreshnessHours { get; set; } = 24;

    /// <summary>
    /// The parents that were older than <see cref="ParentFreshnessHours"/> at the most recent fire, comma separated,
    /// or null when the last fire found all of them current. Purely diagnostic, and rewritten on every fire, so it
    /// answers "was this rebuild fed by everything" without reading the run history of four other schedules.
    /// </summary>
    public string? LastStaleParents { get; set; }

    /// <summary>
    /// The newest parent <see cref="LastFireUtc"/> this chained schedule has already reacted to, or null if it has
    /// never fired behind its current parents. This is the idempotence key of the chain: the scheduler fires only
    /// when the OLDEST of the parents' last fires is newer than this value, then stamps the NEWEST of them.
    /// <para>
    /// Those two ends are deliberately different, and a fan-in is wrong with either alone. Requiring the oldest to
    /// have advanced is what makes the fire wait for every parent rather than react to whichever fired first;
    /// stamping the newest is what stops the next tick seeing the same completed set as new. With a single parent
    /// both reduce to that parent's last fire, so the original chain semantics are unchanged.
    /// </para>
    /// Null on a clock-driven schedule.
    /// </summary>
    public DateTime? LastParentFireUtc { get; set; }

    /// <summary>The IANA time zone the cron expression is evaluated in (for example <c>Europe/Oslo</c>); <c>UTC</c>
    /// by default. Ignored for interval schedules.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>Whether the schedule is active per its definition (the YAML <c>enabled</c> flag or the API create).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether missed occurrences are backfilled. False (the default) skips a fire the host missed and
    /// resumes at the next occurrence after now; true catches up, firing one missed occurrence per scheduler tick
    /// until current. Applied by the scheduler when it advances <see cref="NextFireUtc"/>.</summary>
    public bool Catchup { get; set; }

    /// <summary>
    /// How many of this schedule's members may EXECUTE concurrently, or null (the default) for unbounded. Because a
    /// group's waves are gated, this is the width of the running wave: 1 makes a fire strictly serial. It bounds the
    /// FIRE rather than the estate, so one fragile upstream can be protected without throttling every other source's
    /// throughput. Stamped onto each member run at enqueue (<see cref="CatalogRun.GroupMaxConcurrency"/>) and applied
    /// by the queue's claim gate.
    /// </summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>An API-applied operational pause that is independent of <see cref="Enabled"/>, so a git re-sync of a
    /// <c>yaml</c> schedule does not clear a pause an operator set through the GUI.</summary>
    public bool Paused { get; set; }

    /// <summary><c>yaml</c> (declared in git, overwritten on sync) or <c>api</c> (created through the control plane).</summary>
    public string Source { get; set; } = "api";

    /// <summary>The operation every fire runs its members with: deliver (the default), verify, plan or known-state. A
    /// drift check is a second schedule on the same flows with operation verify.</summary>
    public string Operation { get; set; } = RunParameters.DeliverOperation;

    /// <summary>The flow parameter values every fire runs its members with, as a JSON object; null when the
    /// schedule supplies none. A flow with a required parameter cannot be scheduled without these.</summary>
    public string? ValuesJson { get; set; }

    /// <summary>The repo-relative path of the file this cadence is written in (a flow document with an inline
    /// <c>schedule:</c> block, or a <c>schedules.yaml</c> library file). Null for an API-created schedule, which has
    /// no file behind it. Refreshed by every sync, so it always names the file git currently defines it in.</summary>
    public string? DefinitionPath { get; set; }

    /// <summary>The flow whose inline <c>schedule:</c> block declares this schedule; null when a library file (or the
    /// API) does. The definition read resolves this flow's pipeline row and serves its stored, secret-redacted YAML,
    /// so the document text is never duplicated here.</summary>
    public string? DefinitionFlow { get; set; }

    /// <summary>The declaring library file's text, stored ONLY for a schedule a <c>schedules.yaml</c> defines: a flow
    /// document already lives (redacted) on its pipeline row, but nothing else in the catalog holds a library file,
    /// and without it "show me the YAML behind this schedule" would have no answer. Null otherwise.</summary>
    public string? DefinitionYaml { get; set; }

    /// <summary>When the schedule next fires (UTC). The scheduler claims a schedule by advancing this atomically.</summary>
    public DateTime? NextFireUtc { get; set; }

    public DateTime? LastFireUtc { get; set; }

    /// <summary>The run id the most recent fire enqueued, for tracing a scheduled run back to its schedule. When the
    /// fire ran a whole member set this is the group's first member; <see cref="LastGroupId"/> carries the set.</summary>
    public Guid? LastRunId { get; set; }

    /// <summary>The run group the most recent fire enqueued, when the schedule has more than one member; null for a
    /// single-member schedule (which enqueues a single run) or a schedule that has never fired.</summary>
    public Guid? LastGroupId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// One schedule this schedule chains behind: a single row of a <c>after:</c> declaration. A table rather than a
/// column because a fan-in step names several parents, and because the readiness rule is a set operation over them
/// (every parent complete, oldest fire newer than what was consumed) which a delimited string could not express in
/// the database. Soft links, no FKs, like the rest of the catalog: reconciled from git on every sync.
/// </summary>
public class CatalogScheduleParent
{
    /// <summary>The chained (child) schedule (<see cref="CatalogSchedule.Id"/>).</summary>
    public Guid ScheduleId { get; set; }

    /// <summary>The repo both schedules live in. Parents resolve within one repo only, so this is part of the join
    /// key rather than a convenience column.</summary>
    public Guid RepoId { get; set; }

    /// <summary>The parent schedule's <see cref="CatalogSchedule.Name"/>, as the YAML wrote it.</summary>
    public string ParentName { get; set; } = string.Empty;

    /// <summary>The parent's position in the declaration, preserved so the API and GUI can render the list the way
    /// the author wrote it rather than in whatever order the database returns.</summary>
    public int Ordinal { get; set; }

    /// <summary>The chained schedule this row belongs to.</summary>
    public CatalogSchedule? Schedule { get; set; }
}

/// <summary>
/// One flow's membership of one schedule: the join a flow makes by writing <c>schedule: &lt;name&gt;</c>. This is
/// the ONLY selector for what a fire runs. A schedule fires once and enqueues every member as a single wave-ordered
/// run group, so a member never starts before the flows it depends on; a flow may join several schedules (a nightly
/// full refresh and an hourly subset, say), which is why this is a table and not a column. Soft links, no FKs, like
/// the rest of the catalog: the rows are reconciled from git on every sync.
/// </summary>
public class CatalogScheduleMember
{
    /// <summary>The schedule joined (<see cref="CatalogSchedule.Id"/>).</summary>
    public Guid ScheduleId { get; set; }

    /// <summary>The member flow's stable, repo-scoped pipeline id.</summary>
    public Guid PipelineId { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The member's flow name, carried so a membership reads on its own without joining the pipeline
    /// table (and survives a flow that has not synced yet).</summary>
    public string FlowName { get; set; } = string.Empty;
}

/// <summary>
/// The kinds of catalog happenings the notification pipeline turns into <see cref="CatalogNotificationEvent"/>
/// rows, stored as short lowercase strings (same convention as <see cref="RunStatuses"/>) so a subscription's kind
/// filter is a plain string match.
/// </summary>
public static class NotificationEventKinds
{
    /// <summary>A run reached <c>failed</c>: it executed and errored, or could not execute at all.</summary>
    public const string RunFailed = "run_failed";

    /// <summary>A run was cancelled by an operator (while queued or while executing).</summary>
    public const string RunCancelled = "run_cancelled";

    /// <summary>A group member was skipped because an upstream dependency did not succeed. Distinct from
    /// <see cref="RunFailed"/> so subscribers can mute the (often numerous) downstream echoes of one failure.</summary>
    public const string RunSkipped = "run_skipped";

    /// <summary>Every kind, in display order.</summary>
    public static readonly IReadOnlyList<string> All = [RunFailed, RunCancelled, RunSkipped];

    /// <summary>The kinds a new subscription starts with: real failures, without the skipped-run echoes.</summary>
    public const string DefaultKinds = RunFailed;

    public static bool IsKnown(string kind)
        => kind is RunFailed or RunCancelled or RunSkipped;
}

/// <summary>The channels a notification subscription can deliver over, stored as short lowercase strings.</summary>
public static class NotificationChannels
{
    public const string Email = "email";
    public const string Slack = "slack";

    public static bool IsKnown(string channel) => channel is Email or Slack;
}

/// <summary>How a subscription paces its messages, stored as short lowercase strings.</summary>
public static class NotificationModes
{
    /// <summary>Send as soon as a matching event exists, then hold further sends for the subscription's cooldown;
    /// everything that arrives during the cooldown is coalesced into the next message. The first failure after a
    /// quiet period alerts within one poll tick, while a failure storm caps out at one message per cooldown.</summary>
    public const string Immediate = "immediate";

    /// <summary>Send one combined summary per fixed interval (six hours by default). Windows with no matching
    /// events send nothing.</summary>
    public const string Digest = "digest";

    public static bool IsKnown(string mode) => mode is Immediate or Digest;
}

/// <summary>
/// One notification-worthy happening, detected from the run history by the control plane's notification service:
/// a run that reached a non-success terminal state (failed, cancelled, skipped). Events are the
/// durable, deduplicated middle of the pipeline: detection inserts each (run, kind) at most once (unique index),
/// and every subscription consumes the stream through its own cursor
/// (<see cref="CatalogNotificationSubscription.LastEventId"/>), so a burst of failures is batched per subscriber
/// rather than sent per event, and re-scanning the detection window never duplicates anything.
/// </summary>
public class CatalogNotificationEvent
{
    /// <summary>Monotonic identity (SQL Server IDENTITY): the cursor subscriptions page the stream by.</summary>
    public long Id { get; set; }

    /// <summary>What happened (see <see cref="NotificationEventKinds"/>).</summary>
    public string Kind { get; set; } = string.Empty;

    public Guid RunId { get; set; }

    public Guid? RepoId { get; set; }

    /// <summary>The run's pipeline (soft link, like <see cref="CatalogRun.PipelineId"/>).</summary>
    public Guid PipelineId { get; set; }

    /// <summary>The run group the run belonged to when it was part of a Node/Batch execution; null standalone.</summary>
    public Guid? GroupId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string FlowKind { get; set; } = string.Empty;

    /// <summary>When the happening occurred (the run's end instant, falling back to when its row was written).</summary>
    public DateTime OccurredUtc { get; set; }

    /// <summary>When detection wrote this event.</summary>
    public DateTime DetectedUtc { get; set; }

    /// <summary>The run's error text, secret-redacted upstream (the catalog only ever stores redacted errors). Null when none was recorded.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One user's opt-in to be notified: the channel (email / Slack), the pacing (immediate with a cooldown, or a
/// fixed-interval digest), and what to hear about (event kinds, an optional flow-name pattern). A user can hold
/// several subscriptions (say, an immediate Slack DM for their own flows plus a six-hour email digest of
/// everything). <see cref="LastEventId"/> is the subscription's private cursor over the event stream: it starts at
/// the newest event at creation time, so opting in never replays history, and every message advances it, so no
/// event is ever reported twice to the same subscription.
/// </summary>
public class CatalogNotificationSubscription
{
    public Guid Id { get; set; }

    /// <summary>The owning user (<see cref="CatalogUser.Id"/>); a soft link, matching the rest of the catalog.</summary>
    public Guid UserId { get; set; }

    /// <summary>Where messages go (see <see cref="NotificationChannels"/>).</summary>
    public string Channel { get; set; } = NotificationChannels.Email;

    /// <summary>How messages are paced (see <see cref="NotificationModes"/>).</summary>
    public string Mode { get; set; } = NotificationModes.Immediate;

    /// <summary>The subscribed event kinds, comma-separated (see <see cref="NotificationEventKinds"/>).</summary>
    public string Kinds { get; set; } = NotificationEventKinds.DefaultKinds;

    /// <summary>An optional flow-name filter: one or more comma-separated wildcard patterns (<c>*</c> matches any
    /// run of characters, <c>?</c> one character), matched case-insensitively. Null subscribes to every flow.</summary>
    public string? FlowPattern { get; set; }

    /// <summary>The destination address for an email subscription; null uses the owning user's account email at
    /// send time (so a directory-driven address change is picked up without touching subscriptions).</summary>
    public string? EmailAddress { get; set; }

    /// <summary>The destination for a Slack subscription: a channel id (for example <c>C0123ABCD</c>) to post into
    /// a shared channel, or null to direct-message the user (their Slack account is resolved by email).</summary>
    public string? SlackTarget { get; set; }

    /// <summary>The digest window in minutes (360 = every six hours). Used only in digest mode.</summary>
    public int DigestIntervalMinutes { get; set; } = 360;

    /// <summary>The minimum minutes between immediate messages: the anti-spam floor. Events arriving inside the
    /// cooldown are coalesced into the next message, never dropped. 0 sends every poll tick. Immediate mode only.</summary>
    public int CooldownMinutes { get; set; } = 5;

    /// <summary>A disabled subscription is kept (with its cursor) but never claimed for dispatch. Re-enabling
    /// fast-forwards the cursor past everything that happened while disabled, so it never floods on resume.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The cursor: the highest <see cref="CatalogNotificationEvent.Id"/> this subscription has considered
    /// (matched or not). Events above it are pending.</summary>
    public long LastEventId { get; set; }

    /// <summary>When this subscription may next produce a message. Digest mode: the end of the current window,
    /// advanced by the interval on every claim. Immediate mode: null (or past) means "as soon as an event exists";
    /// each send sets it to now + cooldown. The dispatcher claims a due subscription by advancing this atomically
    /// (compare-and-swap), so multiple control-plane nodes never double-send a window.</summary>
    public DateTime? NextDueUtc { get; set; }

    /// <summary>When this subscription last produced a message; null until the first send.</summary>
    public DateTime? LastSentUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>The lifecycle states of a <see cref="CatalogNotificationDelivery"/>, stored as short lowercase strings.</summary>
public static class NotificationDeliveryStatuses
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>
/// One composed message on its way out (or already out): the notification outbox. Dispatch composes the full
/// content up front (subject, plain text, HTML for email, Block Kit JSON for Slack) and the send loop claims rows
/// atomically, so a message survives restarts, retries transient channel failures with backoff, and is auditable
/// afterwards: what was sent, where, covering which events, and what went wrong if it never made it.
/// </summary>
public class CatalogNotificationDelivery
{
    public Guid Id { get; set; }

    /// <summary>The subscription that produced this message (soft link; the row outlives a deleted subscription).</summary>
    public Guid SubscriptionId { get; set; }

    /// <summary>The owning user, denormalized so the self-service history list is a single-table seek.</summary>
    public Guid UserId { get; set; }

    /// <summary>The channel this message goes out on (see <see cref="NotificationChannels"/>).</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>The resolved destination: an email address, a Slack channel id, or <c>dm:{email}</c> for a Slack
    /// direct message the sender resolves to the user's Slack account at send time.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The email subject / Slack fallback headline.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The plain-text rendering: the email text alternative and the Slack fallback text.</summary>
    public string TextBody { get; set; } = string.Empty;

    /// <summary>The HTML rendering for an email delivery; null for Slack.</summary>
    public string? HtmlBody { get; set; }

    /// <summary>The Block Kit JSON (an array of blocks) for a Slack delivery; null for email.</summary>
    public string? SlackBlocksJson { get; set; }

    /// <summary>How many events this message covers (after the subscription's filters).</summary>
    public int EventCount { get; set; }

    /// <summary>The event-id range this message advanced the subscription's cursor over (audit).</summary>
    public long FirstEventId { get; set; }

    public long LastEventId { get; set; }

    /// <summary>The lifecycle state (see <see cref="NotificationDeliveryStatuses"/>).</summary>
    public string Status { get; set; } = NotificationDeliveryStatuses.Queued;

    /// <summary>How many send attempts have been made (claimed counts as attempted).</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt may run; null means immediately. Set by the retry backoff.</summary>
    public DateTime? NextAttemptUtc { get; set; }

    /// <summary>When the current <c>sending</c> claim was taken; the recovery sweep requeues rows whose claim is
    /// older than the stuck threshold (the claiming node died mid-send).</summary>
    public DateTime? ClaimedUtc { get; set; }

    /// <summary>The most recent send error, secret-redacted; null once sent (or never attempted).</summary>
    public string? LastError { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the message was accepted by the channel; null until sent.</summary>
    public DateTime? SentUtc { get; set; }
}

/// <summary>
/// The notification detector's single-row high-water mark over <see cref="CatalogRun.WrittenUtc"/> (every terminal
/// transition stamps that column with the writer's now). Each detection tick scans the window from a little before
/// the watermark (an overlap that absorbs writer clock skew and in-flight writes) up to now, inserts the events
/// that are not already recorded, and advances the mark: the row's lock is what serializes detection across
/// control-plane replicas, and the event table's (run, kind) unique index is what makes the overlap re-scan free.
/// </summary>
public class CatalogNotificationWatermark
{
    /// <summary>Always <see cref="WellKnownId"/>: the table holds exactly one row.</summary>
    public int Id { get; set; }

    public const int WellKnownId = 1;

    /// <summary>The high-water mark: runs written at or before this instant have been scanned.</summary>
    public DateTime RunsWatermarkUtc { get; set; }

    /// <summary>When the estate digest generator may next produce a scheduled digest: the end of the current
    /// digest window, advanced to the next boundary on every claim. Null until the first tick observes the row
    /// (which arms it without generating, so a fresh deployment never emits a digest over unknown history). The
    /// generator claims a due window by compare-and-swapping this instant, exactly as a subscription window is
    /// claimed, so several control-plane replicas never generate the same digest twice.</summary>
    public DateTime? DigestDueUtc { get; set; }

    /// <summary>The start of the window currently accumulating: the previous scheduled digest's period end.
    /// Stamped with "now" on every claim, so the periods of consecutive scheduled digests chain without a gap.</summary>
    public DateTime? DigestPeriodStartUtc { get; set; }

    /// <summary>The scheduled generator's cursor over the event stream: the highest
    /// <see cref="CatalogNotificationEvent.Id"/> a scheduled digest has covered. Events above it are pending for
    /// the next one, so a window is never skipped and never reported twice, whatever the detection latency.</summary>
    public long DigestCursorEventId { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>What produced a <see cref="CatalogNotificationDigest"/>, stored as a short lowercase string.</summary>
public static class NotificationDigestOrigins
{
    /// <summary>The control plane's own periodic generation: one digest per configured interval, produced whether
    /// or not anybody subscribes, so the estate always has a standing record of what went wrong in each window.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Generated on demand by a person from the GUI over a window they chose. Manual digests are reports
    /// only: they never move the scheduled cursor, so asking for one never robs the next scheduled digest.</summary>
    public const string Manual = "manual";

    public static bool IsKnown(string origin) => origin is Scheduled or Manual;
}

/// <summary>
/// One estate digest: a composed, persisted summary of every notification event in a window, produced
/// independently of any subscription. The control plane generates one per configured interval, and a person can
/// generate one on demand over any window, so "what failed yesterday" is answerable in the GUI on a deployment
/// with no channel configured and nobody subscribed. The rendered bodies are stored (text, HTML and Block Kit),
/// which is what makes a digest both readable in the GUI and sendable to a channel later without recomposing it
/// from events that retention may since have pruned.
/// </summary>
public class CatalogNotificationDigest
{
    public Guid Id { get; set; }

    /// <summary>What produced it (see <see cref="NotificationDigestOrigins"/>).</summary>
    public string Origin { get; set; } = NotificationDigestOrigins.Scheduled;

    /// <summary>The window's start; for a scheduled digest, the previous scheduled digest's period end, so
    /// consecutive periods chain without a gap.</summary>
    public DateTime PeriodStartUtc { get; set; }

    /// <summary>The window's end: the instant the digest was generated at.</summary>
    public DateTime PeriodEndUtc { get; set; }

    /// <summary>When the digest row was written (equal to <see cref="PeriodEndUtc"/> for both origins today, kept
    /// separate because it is the retention and list-ordering column).</summary>
    public DateTime GeneratedUtc { get; set; }

    /// <summary>The user who asked for a manual digest (<see cref="CatalogUser.Id"/>, a soft link); null for a
    /// scheduled one.</summary>
    public Guid? GeneratedByUserId { get; set; }

    /// <summary>How many events the digest covers.</summary>
    public int EventCount { get; set; }

    /// <summary>How many distinct flows those events came from.</summary>
    public int FlowCount { get; set; }

    /// <summary>Per-kind counts, so the digest list reads without parsing a body.</summary>
    public int FailedCount { get; set; }

    public int CancelledCount { get; set; }

    public int SkippedCount { get; set; }

    /// <summary>The event-id range covered; both 0 when the window held no events.</summary>
    public long FirstEventId { get; set; }

    public long LastEventId { get; set; }

    /// <summary>Whether the window held more events than one digest renders; the remainder is covered by the next
    /// scheduled digest (the cursor advanced only over what was taken).</summary>
    public bool Truncated { get; set; }

    /// <summary>The headline, identical to the subject a delivery of this digest would carry.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The plain-text rendering (the GUI's copy view, and an email's text alternative).</summary>
    public string TextBody { get; set; } = string.Empty;

    /// <summary>The HTML rendering: what the GUI displays, and what an email delivery of this digest carries.</summary>
    public string HtmlBody { get; set; } = string.Empty;

    /// <summary>The Block Kit JSON (an array of blocks) a Slack delivery of this digest carries.</summary>
    public string SlackBlocksJson { get; set; } = string.Empty;

    /// <summary>
    /// The window's events grouped per flow, as a JSON array: what the GUI renders as the digest's table. Stored
    /// rather than recomputed because a digest outlives the events behind it (events are pruned in weeks, digests
    /// kept for a year), so a digest read later still shows which flows failed and why, not just a total.
    /// </summary>
    public string GroupsJson { get; set; } = string.Empty;
}
