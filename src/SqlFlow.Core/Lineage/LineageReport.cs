namespace SqlFlow.Core.Lineage;

/// <summary>Where a lineage fact came from. The tiers cross-check each other; every edge carries its tier so
/// the graph is honest about what it knows in each connectivity posture.</summary>
public enum LineageTier
{
    /// <summary>Stated by a flow document (the YAML): what the author intends. Always available.</summary>
    Declared = 0,

    /// <summary>Extracted from the canonical run artifacts (run.json SqlTrace): what actually executed,
    /// stamped with the run. Available offline.</summary>
    Observed = 1,

    /// <summary>Expanded from live catalog metadata and module definitions (sys.sql_modules) parsed as
    /// T-SQL: what the database adds. Needs connectivity.</summary>
    Derived = 2,
}

/// <summary>What kind of catalog object a lineage node is, when known.</summary>
public enum LineageNodeKind
{
    Unknown = 0,
    Table = 1,
    View = 2,
    Procedure = 3,
    Function = 4,
    Trigger = 5,
    Synonym = 6,

    /// <summary>A file endpoint (a file flow's source, an export flow's destination).</summary>
    File = 7,
}

/// <summary>How a flow or module relates to an object (the DeltaForge typed-relation taxonomy).</summary>
public enum LineageRelation
{
    Reads = 0,
    Writes = 1,
    Creates = 2,

    /// <summary>Needs the object to exist (EXEC of a procedure, a TVF call, ALTER).</summary>
    Requires = 3,

    Destroys = 4,
}

/// <summary>One pipeline (flow document) participating in the graph.</summary>
public sealed record LineageFlowNode
{
    public required string Name { get; init; }

    /// <summary>The document kind: file/ing/exp/sp/inv/hc.</summary>
    public required string Kind { get; init; }

    /// <summary>The flow document path, relative to the scanned folder.</summary>
    public required string File { get; init; }

    public string? Batch { get; init; }

    /// <summary>The flow's execution mode (today only a health-check flow can declare <c>mode: manual</c>).
    /// Manual flows are excluded from automatic dispatch (local batch membership, control-plane group
    /// expansion, the scheduler); a direct single-flow trigger runs them regardless.</summary>
    public Runs.ExecutionMode Mode { get; init; } = Runs.ExecutionMode.Auto;
}

/// <summary>One catalog or file object participating in the graph. The key is the canonical node identity:
/// server reference, database, schema, and name, case-folded; raw spellings are preserved here.</summary>
public sealed record LineageObjectNode
{
    public required string Key { get; init; }

    /// <summary>The server identity: the connection REFERENCE the object was reached through (a
    /// ${env:...} reference, an @alias, or an inline string's redacted identity). Two documents naming the
    /// same reference name the same server; distinct references are distinct servers by construction.</summary>
    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public LineageNodeKind Kind { get; init; } = LineageNodeKind.Unknown;

    /// <summary>The module body (the <c>sys.sql_modules</c> definition) for a view/procedure/function/trigger,
    /// captured by the derived (connected) tier. Null for a plain table, an offline tier, or an encrypted module.</summary>
    public string? Definition { get; init; }

    /// <summary>The generating DDL of the object (the <c>CREATE TABLE</c>/<c>CREATE VIEW</c> we ran), captured
    /// from the observed run trace or a declared hook so the catalog holds the script offline. Null when no
    /// tier saw the object created (a pre-existing source table). Distinct from <see cref="Definition"/>: that
    /// is the live module body read from the database, this is the script the engine emitted.</summary>
    public string? Script { get; init; }

    /// <summary>The tier that supplied <see cref="Script"/>, or null when there is no script.</summary>
    public LineageTier? ScriptTier { get; init; }

    /// <summary>The object's columns: the derived (connected) tier reads them live; offline they are parsed
    /// from the CREATE TABLE the run executed. Empty when no tier saw them.</summary>
    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    /// <summary>The tier that supplied <see cref="Columns"/>, or null when there are none. The catalog uses it
    /// to keep a live (derived) dictionary from being overwritten by an offline (observed) one.</summary>
    public LineageTier? ColumnsTier { get; init; }

    /// <summary>Node-scoped findings: an encrypted module whose definition is unreadable, an ambiguous
    /// database resolution, a linked-server reference that could not be expanded.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>One column of a catalog object, as the derived (connected) tier read it from the live database.</summary>
public sealed record LineageColumn
{
    /// <summary>1-based column position.</summary>
    public required int Ordinal { get; init; }

    public required string Name { get; init; }

    /// <summary>The SQL Server type rendered with length/precision (for example <c>nvarchar(100)</c>).</summary>
    public string? DataType { get; init; }

    public bool Nullable { get; init; }
}

/// <summary>One attributed lineage fact: a flow or module relating to an object.</summary>
public sealed record LineageEdge
{
    /// <summary>The flow this fact belongs to; null for a module-derived fact.</summary>
    public string? Flow { get; init; }

    /// <summary>The module (view/procedure/function key) whose definition produced this fact; null for a
    /// flow-level fact.</summary>
    public string? ViaModule { get; init; }

    public required LineageRelation Relation { get; init; }

    /// <summary>The object the relation points at (a <see cref="LineageObjectNode.Key"/>).</summary>
    public required string ObjectKey { get; init; }

    public required LineageTier Tier { get; init; }

    /// <summary>The run that observed this fact (observed tier only).</summary>
    public Guid? ObservedRunId { get; init; }

    public DateTime? ObservedAtUtc { get; init; }

    /// <summary>The trace step that produced the statement (observed tier only).</summary>
    public string? Step { get; init; }
}

/// <summary>A flow-level dependency: ToFlow must wait for FromFlow, because of the named objects.</summary>
public sealed record LineageFlowDependency
{
    public required string FromFlow { get; init; }

    public required string ToFlow { get; init; }

    /// <summary>The object keys that mediate the dependency.</summary>
    public required IReadOnlyList<string> ViaObjects { get; init; }
}

/// <summary>One concurrency wave: everything in it can run together once the previous wave finished.</summary>
public sealed record LineageWave
{
    public required int Wave { get; init; }

    public required IReadOnlyList<string> Flows { get; init; }
}

/// <summary>The objective of the calculation: what order, what runs concurrently, what must wait.</summary>
public sealed record LineageExecutionPlan
{
    public required IReadOnlyList<LineageWave> Waves { get; init; }

    /// <summary>Flows whose mutual order could not be resolved (dependency-cycle members). They are
    /// scheduled together in the final fallback wave, with the cycle reported: the DeltaForge behavior of
    /// degrading loudly instead of refusing to plan.</summary>
    public required IReadOnlyList<string> Unordered { get; init; }
}

/// <summary>A dependency cycle: ordering is undecidable for these flows until an edge is broken.</summary>
public sealed record LineageCycle
{
    /// <summary>The flows in the cycle, in path order (the last depends on the first).</summary>
    public required IReadOnlyList<string> Flows { get; init; }

    /// <summary>The objects mediating the cycle's edges.</summary>
    public required IReadOnlyList<string> ViaObjects { get; init; }
}

/// <summary>One flow name declared by more than one document. The graph collapses the colliding documents to a
/// single flow node (first wins) and warns, so the report still plans; this record keeps the collision in a
/// structured form so a stricter consumer (batch membership) can refuse it without parsing warning text.</summary>
public sealed record LineageDuplicateFlowName
{
    /// <summary>The shared flow name (the casing of the first declaring document).</summary>
    public required string Name { get; init; }

    /// <summary>Every document that declared the name, as repository-relative paths, ordinal-sorted.</summary>
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>
/// The canonical lineage artifact (<c>lineage.json</c>): the aggregated graph of every pipeline and object,
/// with full provenance, plus the execution plan derived from it. Like the run artifacts, the envelope is a
/// stable, versioned contract: the file can be charted, diffed, or bulk-loaded without per-version parsing,
/// and full mode persists the same shape to the control database later.
/// </summary>
public sealed record LineageReport
{
    /// <summary>The current lineage.json schema version written by this build. Version 2 adds the object
    /// <see cref="LineageObjectNode.Script"/> and its offline columns: a v1 reader sees the same shape plus
    /// new fields it can ignore.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>The scanned flow directory.</summary>
    public required string FlowDirectory { get; init; }

    /// <summary>Which tiers fed this graph (the connectivity posture of the computation).</summary>
    public required IReadOnlyList<LineageTier> TiersUsed { get; init; }

    public required IReadOnlyList<LineageFlowNode> Flows { get; init; }

    public required IReadOnlyList<LineageObjectNode> Objects { get; init; }

    public required IReadOnlyList<LineageEdge> Edges { get; init; }

    public required IReadOnlyList<LineageFlowDependency> FlowDependencies { get; init; }

    public required LineageExecutionPlan ExecutionPlan { get; init; }

    public required IReadOnlyList<LineageCycle> Cycles { get; init; }

    /// <summary>Flow names declared by more than one document, with every declaring file. The graph keeps only
    /// the first declaration (the other facts merge under it) and also warns; this is the same collision in a
    /// structured form. Empty when every flow name is unique.</summary>
    public required IReadOnlyList<LineageDuplicateFlowName> DuplicateFlowNames { get; init; }

    /// <summary>Graph-scoped findings: multi-writer objects, stale observations, parse warnings, unhandled
    /// statement kinds. Never silently dropped.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
