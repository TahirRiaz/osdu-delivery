using SqlFlow.Core.Connections;

namespace SqlFlow.Core.Maintenance;

/// <summary>
/// How wide a maintenance action looks. <see cref="Database"/> examines every user object in the scoped
/// database, <see cref="Schema"/> narrows to one schema, and <see cref="SingleObject"/> narrows to one table
/// or view. An action declares the widest and narrowest scope it accepts through its
/// <see cref="MaintenanceActionDescriptor"/>, so a request that would make the engine walk more than the
/// action can afford, or narrow it past what its measurement can honour, is refused before it runs.
/// </summary>
public enum MaintenanceScopeLevel
{
    Database,
    Schema,
    SingleObject,
}

/// <summary>How urgent a finding is. The three levels match the insights advisories, so a maintenance report
/// and the attention list can be ranked against each other without a translation table.</summary>
public enum MaintenanceSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// The part of the warehouse one maintenance action looks at: a database, optionally narrowed to a schema and
/// then to a single object. Every member is an unquoted identifier as the operator typed it; the actions quote
/// them when they build SQL, and never concatenate one into a predicate without quoting.
/// </summary>
public sealed record MaintenanceScope
{
    /// <summary>The database to run in; null means the connection's own current database.</summary>
    public string? Database { get; init; }

    /// <summary>The schema to narrow to; null means every schema.</summary>
    public string? Schema { get; init; }

    /// <summary>The single object to narrow to; null means every object in the schema scope. Requires
    /// <see cref="Schema"/>, which the request validation enforces.</summary>
    public string? ObjectName { get; init; }

    /// <summary>The narrowest level this scope actually pins down.</summary>
    public MaintenanceScopeLevel Level => ObjectName is not null
        ? MaintenanceScopeLevel.SingleObject
        : Schema is not null ? MaintenanceScopeLevel.Schema : MaintenanceScopeLevel.Database;

    /// <summary>A human label for the report ("database dwh", "schema arc", "object arc.Sales").</summary>
    public string Describe() => Level switch
    {
        MaintenanceScopeLevel.SingleObject => $"object {Schema}.{ObjectName}",
        MaintenanceScopeLevel.Schema => $"schema {Schema}",
        _ => Database is null ? "database (connection default)" : $"database {Database}",
    };
}

/// <summary>
/// One thing a maintenance action found worth an operator's attention, with the evidence numbers that support
/// it and (where one exists) the statement that would act on it. The statement is a SUGGESTION for human
/// review: the maintenance family is read-only by design and never executes it.
/// </summary>
public sealed record MaintenanceFinding
{
    public required MaintenanceSeverity Severity { get; init; }

    /// <summary>A short machine-usable category ("fragmentation", "staleStatistics", "heap"), stable across
    /// releases so a client can group or filter findings without parsing the title.</summary>
    public required string Category { get; init; }

    /// <summary>What the finding is about, as the operator reads it: "arc.Ferde_Passeringer" for an object,
    /// "arc.Ferde_Passeringer.IX_Dato" for an index, a database name for a database-wide finding.</summary>
    public required string Target { get; init; }

    /// <summary>One sentence stating the finding with its evidence numbers inline.</summary>
    public required string Detail { get; init; }

    /// <summary>The measurements behind the finding, keyed by a short camelCase name. Numbers stay numbers so
    /// a client can sort or threshold on them instead of re-parsing <see cref="Detail"/>.</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>The statement that would act on the finding, ready for review; null when the finding is
    /// informational or no single statement addresses it. NEVER executed by SQLFlow.</summary>
    public string? SuggestedSql { get; init; }
}

/// <summary>
/// The outcome of one maintenance action: what ran, over what, what it found, and the review script its
/// findings add up to. <see cref="Detail"/> carries the action's own native payload for clients that want the
/// full measurement rather than the ranked findings, and is the shape the four warehouse-health compute
/// operations have always returned.
/// </summary>
public sealed record MaintenanceReport
{
    public required string Action { get; init; }

    public required string Title { get; init; }

    /// <summary>The database the action measured, as the server reported it.</summary>
    public string? Database { get; init; }

    /// <summary>The scope the action ran under, as <see cref="MaintenanceScope.Describe"/> renders it.</summary>
    public required string Scope { get; init; }

    /// <summary>How many candidates the action examined (objects, indexes, statistics, cached statements). The
    /// denominator for the finding count: 3 findings out of 4000 indexes reads very differently from 3 out
    /// of 5.</summary>
    public required int ItemsExamined { get; init; }

    /// <summary>True when the action stopped at its row limit, so the report is a page and not the whole
    /// picture. A client must say so rather than presenting a truncated list as complete.</summary>
    public bool Truncated { get; init; }

    public required IReadOnlyList<MaintenanceFinding> Findings { get; init; }

    /// <summary>Every distinct <see cref="MaintenanceFinding.SuggestedSql"/> in finding order: the review
    /// script for this action, deduplicated. Empty when nothing needs doing.</summary>
    public required IReadOnlyList<string> SuggestedSql { get; init; }

    /// <summary>Caveats the operator needs to read the numbers correctly (the window the DMV counters cover,
    /// a sampling mode, a permission that limited the scan).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The action's native measurement payload, serialized as-is onto the task result. For the four
    /// warehouse-health actions this IS the historical result shape, so existing readers keep working.</summary>
    public object? Detail { get; init; }

    /// <summary>
    /// Set when the action stopped and asked instead of measuring: it could not determine what to measure and
    /// declined to guess. The task SUCCEEDS carrying this (the action did its job, which was to establish that
    /// a decision is needed), so a client must check it before reading the findings as a verdict.
    /// </summary>
    public MaintenanceQuestion? Question { get; init; }

    /// <summary>True when the report is a question rather than a measurement.</summary>
    public bool AwaitingInput => Question is not null;

    public int CriticalCount => Findings.Count(f => f.Severity == MaintenanceSeverity.Critical);

    public int WarningCount => Findings.Count(f => f.Severity == MaintenanceSeverity.Warning);
}

/// <summary>
/// One tunable an action accepts, described well enough that a GUI can render a form and an MCP client can
/// fill the field without reading source. Declared by the action, surfaced through the actions endpoint.
/// </summary>
public sealed record MaintenanceParameter(string Name, string Description, double Minimum, double Maximum, double Default);

/// <summary>
/// A decision the action could not make for the operator, and stopped rather than guessing. An action that
/// cannot determine what to measure asks instead of picking something plausible: a duplicate check run against
/// the wrong key answers confidently and wrongly, which is worse than not answering. A client presents
/// <see cref="Prompt"/>, collects a value for <see cref="Parameter"/> (choosing from <see cref="Options"/> when
/// they are offered), and re-runs the action.
/// </summary>
public sealed record MaintenanceQuestion
{
    /// <summary>The question, phrased for the person who has to answer it.</summary>
    public required string Prompt { get; init; }

    /// <summary>The request field the answer goes into ("columns").</summary>
    public required string Parameter { get; init; }

    /// <summary>The values worth choosing from, when the action can enumerate them; empty when the answer is
    /// free-form. Never a default the action would silently apply.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
}

/// <summary>
/// A validated ask for one maintenance action: which action, over what scope, with the shared tunables. The
/// per-action tunables ride in <see cref="Thresholds"/> so adding an action never changes this contract or
/// the compute payload that carries it.
/// </summary>
public sealed record MaintenanceRequest
{
    public required string Action { get; init; }

    public MaintenanceScope Scope { get; init; } = new();

    /// <summary>The most rows the action may return. Bounds both the query and the stored result.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Per-action tunables, keyed by the parameter names the action declares. Unknown keys are a
    /// request error, so a typo never silently runs with a default.</summary>
    public IReadOnlyDictionary<string, double> Thresholds { get; init; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The columns the action works over, for an action that declares
    /// <see cref="MaintenanceActionDescriptor.AcceptsColumns"/>. Empty lets the action derive them; where it
    /// cannot, it answers with a <see cref="MaintenanceQuestion"/> rather than guessing.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The most columns a request may name.</summary>
    public const int MaxColumns = 16;

    /// <summary>Reads a declared threshold, falling back to the action's default.</summary>
    public double Threshold(string name, double fallback)
        => Thresholds.TryGetValue(name, out var value) ? value : fallback;
}

/// <summary>
/// Everything an action needs to run: the resolved connection (a real connection string, produced on the node
/// from the task's reference), the provider it resolved to, and the validated request. The control plane never
/// constructs one of these; it only ever enqueues the reference.
/// </summary>
public sealed record MaintenanceExecutionContext
{
    public required string ConnectionString { get; init; }

    public required DataSourceKind Kind { get; init; }

    public required MaintenanceRequest Request { get; init; }

    public MaintenanceScope Scope => Request.Scope;
}

/// <summary>
/// One standard warehouse maintenance task. Implementations are read-only: they MEASURE the warehouse and
/// emit findings with review-ready SQL, and never execute a mutating statement. That is the whole contract of
/// the family, and it is what lets the surface be exposed to an assistant safely.
/// </summary>
public interface IMaintenanceAction
{
    /// <summary>What this action is and what it accepts. The one place its metadata lives; the control plane
    /// validates requests and answers the discovery endpoint from the same descriptor.</summary>
    MaintenanceActionDescriptor Descriptor { get; }

    /// <summary>Runs the measurement and returns the report. Honors cancellation throughout.</summary>
    Task<MaintenanceReport> RunAsync(MaintenanceExecutionContext context, CancellationToken ct);
}
