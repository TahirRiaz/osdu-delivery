using SqlFlow.Core.Comparison;
using SqlFlow.Core.Connections;

namespace SqlFlow.Core.Maintenance;

/// <summary>
/// Everything about one maintenance action that is NOT its query: the name a request calls it by, what it
/// measures, how wide a scope it accepts, which providers it runs on, and the tunables it reads. The single
/// contract shared by the control plane (which validates a request and answers the discovery endpoint from it,
/// without referencing any provider) and the node (whose implementation carries the same descriptor), so the
/// two ends cannot drift on what an action accepts.
/// </summary>
public sealed record MaintenanceActionDescriptor
{
    /// <summary>The stable camelCase name a request names the action by.</summary>
    public required string Name { get; init; }

    /// <summary>A short human title for a report header or a GUI list entry.</summary>
    public required string Title { get; init; }

    /// <summary>What the action measures and what its findings mean.</summary>
    public required string Description { get; init; }

    /// <summary>The widest scope the action accepts: an action whose cost is proportional to the whole database
    /// allows <see cref="MaintenanceScopeLevel.Database"/>; one that must be pointed at a single object
    /// requires <see cref="MaintenanceScopeLevel.SingleObject"/>.</summary>
    public required MaintenanceScopeLevel WidestScope { get; init; }

    /// <summary>The NARROWEST scope the action can honour. The four warehouse-health probes read server- and
    /// database-scoped DMVs whose rows are ranked and truncated before any schema is known, so narrowing them
    /// after the fact would silently report a page as the whole picture; they pin this to
    /// <see cref="MaintenanceScopeLevel.Database"/> and refuse a narrower ask instead.</summary>
    public MaintenanceScopeLevel NarrowestScope { get; init; } = MaintenanceScopeLevel.SingleObject;

    /// <summary>The providers the action runs on. Every action shipped today is authored in T-SQL.</summary>
    public IReadOnlyList<DataSourceKind> SupportedKinds { get; init; }
        = [DataSourceKind.MSSQL, DataSourceKind.AZDB];

    /// <summary>The tunables the action reads out of <see cref="MaintenanceRequest.Thresholds"/>.</summary>
    public IReadOnlyList<MaintenanceParameter> Parameters { get; init; } = [];

    /// <summary>True when the action is one of the four warehouse-health probes that also back the insights
    /// recommendations, and therefore keeps its historical standalone compute operation and result shape.</summary>
    public bool IsWarehouseHealthProbe { get; init; }

    /// <summary>A caveat worth showing before the action is run (a heavy scan, a permission it needs). Null
    /// when the action is unremarkable to run.</summary>
    public string? Caution { get; init; }

    /// <summary>True when the action works over a named column list, which a request may supply through
    /// <see cref="MaintenanceRequest.Columns"/>. An action that does not declare this refuses a request that
    /// names columns, so a caller learns the field is ignored rather than assuming it applied.</summary>
    public bool AcceptsColumns { get; init; }

    public bool SupportsKind(DataSourceKind kind) => SupportedKinds.Contains(kind);
}

/// <summary>
/// The closed set of standard warehouse maintenance tasks SQLFlow can run, as descriptors. Read-only by
/// design: every action MEASURES the warehouse and emits review-ready SQL, and none of them executes a
/// mutating statement. The implementations live with their provider (SQL Server today); this catalog is the
/// contract, so the control plane can validate a request and list the surface without referencing a provider.
/// </summary>
public static class MaintenanceActions
{
    /// <summary>Missing-index advisories from the engine's own tuning DMVs. Also a warehouse-health probe.</summary>
    public const string MissingIndexes = "missingIndexes";

    /// <summary>Statistics freshness against the engine's dynamic auto-update threshold. Also a probe.</summary>
    public const string StatisticsHealth = "statisticsHealth";

    /// <summary>Per-index read/write usage, surfacing write-only indexes. Also a probe.</summary>
    public const string IndexUsage = "indexUsage";

    /// <summary>The plan cache's most expensive statements. Also a probe.</summary>
    public const string TopQueries = "topQueries";

    /// <summary>Index fragmentation and page density, with REBUILD/REORGANIZE suggestions.</summary>
    public const string IndexFragmentation = "indexFragmentation";

    /// <summary>Row counts and storage per table, with the compression that would apply.</summary>
    public const string TableSpace = "tableSpace";

    /// <summary>Tables with no clustered index (heaps), and their forwarded-record cost.</summary>
    public const string HeapTables = "heapTables";

    /// <summary>Untrusted foreign keys and check constraints the optimizer must ignore.</summary>
    public const string ConstraintTrust = "constraintTrust";

    /// <summary>Duplicate rows on one table's key, using the key the table itself declares.</summary>
    public const string DuplicateKeys = "duplicateKeys";

    /// <summary>Every descriptor, in the order a client should present them.</summary>
    public static readonly IReadOnlyList<MaintenanceActionDescriptor> All =
    [
        new()
        {
            Name = MissingIndexes,
            Title = "Missing indexes",
            Description =
                "Index advisories the optimizer recorded while compiling real queries, ranked by the standard " +
                "seeks-times-cost-times-impact improvement measure, each with a CREATE INDEX statement. The " +
                "advisories are evidence from real compilations, not a guess, but they overlap each other and " +
                "ignore write cost, so they are a starting point for index design rather than a work list.",
            WidestScope = MaintenanceScopeLevel.Database,
            NarrowestScope = MaintenanceScopeLevel.Database,
            IsWarehouseHealthProbe = true,
        },
        new()
        {
            Name = StatisticsHealth,
            Title = "Statistics freshness",
            Description =
                "How far each statistics object has drifted since it was last built, measured against the " +
                "engine's own dynamic auto-update threshold, with an UPDATE STATISTICS statement for every " +
                "stale entry. Stale statistics are the usual cause of a plan that was fast last month.",
            WidestScope = MaintenanceScopeLevel.Database,
            NarrowestScope = MaintenanceScopeLevel.Database,
            IsWarehouseHealthProbe = true,
        },
        new()
        {
            Name = IndexUsage,
            Title = "Index usage",
            Description =
                "Reads and writes per index since the counters last reset, surfacing indexes that cost every " +
                "load a maintenance write but served no read in that window. Judge a drop candidate against " +
                "the window length the report carries: a short window after a restart proves nothing.",
            WidestScope = MaintenanceScopeLevel.Database,
            NarrowestScope = MaintenanceScopeLevel.Database,
            IsWarehouseHealthProbe = true,
        },
        new()
        {
            Name = TopQueries,
            Title = "Most expensive statements",
            Description =
                "The plan cache's statements ranked by total elapsed time, with execution counts, CPU and " +
                "logical reads. The cache is a rolling window, so this answers what is expensive lately, not " +
                "what was expensive ever.",
            WidestScope = MaintenanceScopeLevel.Database,
            NarrowestScope = MaintenanceScopeLevel.Database,
            IsWarehouseHealthProbe = true,
        },
        new()
        {
            Name = IndexFragmentation,
            Title = "Index fragmentation",
            Description =
                "Logical fragmentation and page fullness per index, with a REORGANIZE or REBUILD suggestion " +
                "for indexes past the thresholds. Small indexes are excluded by page count because " +
                "fragmentation on a handful of pages is noise, not a cost.",
            WidestScope = MaintenanceScopeLevel.Database,
            Caution =
                "Scans index metadata in LIMITED mode, which reads the parent level of each B-tree. Cheap for " +
                "a schema, heavier over a whole large warehouse; narrow the scope if it times out.",
            Parameters =
            [
                new("reorganizeThreshold", "Fragmentation percent at or above which REORGANIZE is suggested.", 1, 100, 5),
                new("rebuildThreshold", "Fragmentation percent at or above which REBUILD is suggested instead.", 1, 100, 30),
                new("minimumPages", "Indexes smaller than this many pages are ignored.", 1, 1_000_000, 1000),
            ],
        },
        new()
        {
            Name = TableSpace,
            Title = "Table space and compression",
            Description =
                "Rows, reserved space, and data-versus-index split per table, largest first, with the row " +
                "compression each uncompressed table would take. The inventory behind a storage conversation, " +
                "and the fastest way to see which tables a warehouse actually consists of.",
            WidestScope = MaintenanceScopeLevel.Database,
            Parameters =
            [
                new("minimumMb", "Tables smaller than this many megabytes are ignored.", 0, 1_000_000, 0),
            ],
        },
        new()
        {
            Name = HeapTables,
            Title = "Heap tables",
            Description =
                "User tables with no clustered index, with their rows and reserved space. A heap in a warehouse " +
                "is nearly always an accident of a load that created the table implicitly; it cannot be seeked, " +
                "and deletes leave it holding space that no rebuild reclaims until it gets a clustered index.",
            WidestScope = MaintenanceScopeLevel.Database,
            Parameters =
            [
                new("minimumRows", "Heaps with fewer rows than this are ignored.", 0, 1_000_000_000, 1000),
            ],
        },
        new()
        {
            Name = ConstraintTrust,
            Title = "Untrusted constraints",
            Description =
                "Foreign keys and check constraints the engine has marked not-trusted, which happens after a " +
                "bulk load or a NOCHECK re-enable. The optimizer ignores an untrusted constraint entirely, so " +
                "it stops eliminating joins and predicates it could otherwise prove redundant.",
            WidestScope = MaintenanceScopeLevel.Database,
        },
        new()
        {
            Name = DuplicateKeys,
            Title = "Duplicate keys",
            Description =
                "Whether one table holds more than one row per key: the group count, the excess rows, and the " +
                "worst offending key values. The key is the one the TABLE declares, in this order: SQLFlow's " +
                "own NCI_KeyColumn index (the business key the load merges on), then a primary key that is not " +
                "a bare identity, then any other unique index or constraint. A surrogate identity key is never " +
                "used: it is unique by construction, so it would report zero duplicates on every table and " +
                "prove nothing. When the table declares no usable key the action ASKS which columns to use " +
                "rather than guessing, because a duplicate check on the wrong key is confidently wrong.",
            WidestScope = MaintenanceScopeLevel.SingleObject,
            AcceptsColumns = true,
            Caution =
                "Groups the whole table by its key, so the cost is a full scan plus a sort or hash. The key " +
                "index usually makes that a streaming aggregate; on a table without one it is a full sort.",
        },
    ];

    /// <summary>The action names, for a validation message.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(a => a.Name).ToArray();

    /// <summary>Finds a descriptor by name (ordinal, camelCase as declared); null when unknown.</summary>
    public static MaintenanceActionDescriptor? Find(string? name)
        => name is null ? null : All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Validates a request against its action's descriptor, throwing <see cref="SqlFlowException"/> with the
    /// offending field named. Called at BOTH ends: the control-plane endpoint (so a bad ask is a 400, never a
    /// queued task doomed to fail) and the node before it opens a connection.
    /// </summary>
    public static MaintenanceActionDescriptor Validate(MaintenanceRequest request, DataSourceKind? kind)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = Find(request.Action)
            ?? throw new SqlFlowException(
                $"Unknown maintenance action '{request.Action}'. Valid actions: {string.Join(", ", Names)}.");

        // The kind gate runs at enqueue only for an explicitly named kind; an @alias resolves its kind on the
        // node, which re-validates there against the RESOLVED kind.
        if (kind is { } declared && !descriptor.SupportsKind(declared))
        {
            throw new SqlFlowException(
                $"The maintenance action '{descriptor.Name}' is authored in T-SQL; the source must be one of " +
                $"{string.Join(", ", descriptor.SupportedKinds)}, not {declared}.");
        }

        var scope = request.Scope;
        if (scope.ObjectName is not null && scope.Schema is null)
        {
            throw new SqlFlowException("A maintenance scope naming an object must also name its schema.");
        }

        if (scope.Level < descriptor.WidestScope)
        {
            throw new SqlFlowException(
                $"The maintenance action '{descriptor.Name}' needs a scope of at least " +
                $"{Describe(descriptor.WidestScope)}; the request scoped only {Describe(scope.Level)}.");
        }

        if (scope.Level > descriptor.NarrowestScope)
        {
            throw new SqlFlowException(
                $"The maintenance action '{descriptor.Name}' measures a whole " +
                $"{Describe(descriptor.NarrowestScope)} and cannot be narrowed to {Describe(scope.Level)}: its " +
                "rows are ranked and truncated before a schema is known, so a narrowed answer would be a page " +
                "presented as the whole picture. Run it database-scoped and filter the findings.");
        }

        if (request.Columns.Count > 0)
        {
            if (!descriptor.AcceptsColumns)
            {
                throw new SqlFlowException(
                    $"The maintenance action '{descriptor.Name}' does not work over a named column list, but " +
                    $"{request.Columns.Count} column(s) were supplied.");
            }

            if (request.Columns.Count > MaintenanceRequest.MaxColumns)
            {
                throw new SqlFlowException(
                    $"columns names {request.Columns.Count} columns; at most {MaintenanceRequest.MaxColumns} are allowed.");
            }

            foreach (var column in request.Columns)
            {
                SqlFragmentGuard.ValidateIdentifier(column, "columns");
            }
        }

        foreach (var (name, value) in request.Thresholds)
        {
            var parameter = descriptor.Parameters
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new SqlFlowException(
                    descriptor.Parameters.Count == 0
                        ? $"The maintenance action '{descriptor.Name}' takes no thresholds, but '{name}' was supplied."
                        : $"'{name}' is not a threshold of the maintenance action '{descriptor.Name}'. Valid: " +
                          $"{string.Join(", ", descriptor.Parameters.Select(p => p.Name))}.");

            if (double.IsNaN(value) || value < parameter.Minimum || value > parameter.Maximum)
            {
                throw new SqlFlowException(
                    $"The threshold '{parameter.Name}' must be between {parameter.Minimum} and {parameter.Maximum}.");
            }
        }

        return descriptor;
    }

    private static string Describe(MaintenanceScopeLevel level) => level.ToString().ToLowerInvariant();
}
