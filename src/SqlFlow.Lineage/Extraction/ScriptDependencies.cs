using SqlFlow.Core.Lineage;

namespace SqlFlow.Lineage.Extraction;

/// <summary>What a statement did to a table: the operation taxonomy the typed relations derive from.</summary>
public enum TableOperation
{
    Read = 0,
    Insert = 1,
    Update = 2,
    Delete = 3,
    Merge = 4,
    Create = 5,
    Alter = 6,
    Drop = 7,
    Truncate = 8,
    Execute = 9,
    BulkInsert = 10,

    /// <summary>Creation and initial population in one statement (SELECT INTO, Synapse/Fabric CTAS): the
    /// DeltaForge CreateAs operation, with its own staging and lifecycle rules.</summary>
    CreateAs = 11,
}

/// <summary>How a table was referenced (the DeltaForge dependency-type taxonomy): directly in a FROM/target,
/// inside a subquery (the only place an object-level self-edge is legitimate), or through a change-tracking
/// surface (CHANGETABLE, trigger inserted/deleted pseudo-tables).</summary>
public enum DependencyKind
{
    Direct = 0,
    Subquery = 1,
    ChangeTracking = 2,
}

/// <summary>One table the script touched, with everything done to it.</summary>
public sealed class TableDependency
{
    public required TableName Table { get; init; }

    public HashSet<TableOperation> Operations { get; } = [];

    public DependencyKind Kind { get; set; } = DependencyKind.Direct;
}

/// <summary>A flow-of-data pair inside one statement: rows moved from Source into Target.</summary>
public sealed record DataFlowPair
{
    public required TableName Source { get; init; }

    public required TableName Target { get; init; }
}

/// <summary>
/// Everything the extractor learned from one script: the DeltaForge ScriptDependencies equivalent.
/// Inbound = read from, outbound = written/created/dropped, local deps = the per-name source lists that let
/// phase two resolve CTEs, temp tables, and views to their base objects, warnings = the statically
/// undecidable and the unhandled, loudly.
/// </summary>
public sealed class ScriptDependencies
{
    private readonly Dictionary<string, TableDependency> _inbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TableDependency> _outbound = new(StringComparer.Ordinal);

    public IReadOnlyCollection<TableDependency> Inbound => _inbound.Values;

    public IReadOnlyCollection<TableDependency> Outbound => _outbound.Values;

    /// <summary>Source names per written/created name (case-folded keys): the phase-two resolution graph.
    /// Recorded for CTE definitions and insert-like movements (INSERT...SELECT, SELECT INTO, MERGE), the
    /// DeltaForge record_local_dep rule: in-place UPDATE/DELETE record nothing here.</summary>
    public Dictionary<string, List<string>> LocalDeps { get; } = new(StringComparer.Ordinal);

    /// <summary>Names created by this script (CTEs, temp tables, SELECT INTO targets, CREATE TABLE/VIEW):
    /// excluded from the additive inbound pass of <see cref="EffectiveInbound"/>.</summary>
    public HashSet<string> ScriptCreated { get; } = new(StringComparer.Ordinal);

    /// <summary>SELECT INTO targets (the T-SQL CTAS): script-internal staging by the DeltaForge rule, so
    /// later statements reading them produce no data-flow pairs.</summary>
    public HashSet<string> CtasCreated { get; } = new(StringComparer.Ordinal);

    /// <summary>Names dropped by this script.</summary>
    public HashSet<string> ScriptDropped { get; } = new(StringComparer.Ordinal);

    /// <summary>Names created and LATER dropped (in that order): the engine's run-scoped staging signature
    /// (create/load/read/drop). Order matters: drop-then-create is a full REBUILD and must keep its
    /// relations, which an order-blind created-and-dropped test would erase.</summary>
    public HashSet<string> CreatedThenDropped { get; } = new(StringComparer.Ordinal);

    /// <summary>Per-statement source-to-target movements: insert-like statements only, three-part names on
    /// both sides, subquery/change-feed self-pairs exempted (the DeltaForge extract_data_flow_edges rules).</summary>
    public List<DataFlowPair> DataFlowPairs { get; } = [];

    public List<string> Warnings { get; } = [];

    public TableDependency AddInbound(TableName table, TableOperation operation, DependencyKind kind)
    {
        var dependency = GetOrAdd(_inbound, table, kind);
        dependency.Operations.Add(operation);

        // Direct dominates: the subquery/change-tracking self-edge exemption (the DeltaForge rule) only
        // holds when EVERY reference to the table was of the exempt kind.
        if (kind == DependencyKind.Direct)
        {
            dependency.Kind = DependencyKind.Direct;
        }

        return dependency;
    }

    public TableDependency AddOutbound(TableName table, TableOperation operation)
    {
        var dependency = GetOrAdd(_outbound, table, DependencyKind.Direct);
        dependency.Operations.Add(operation);
        return dependency;
    }

    private static TableDependency GetOrAdd(Dictionary<string, TableDependency> side, TableName table, DependencyKind kind)
    {
        if (!side.TryGetValue(table.Key, out var dependency))
        {
            dependency = new TableDependency { Table = table, Kind = kind };
            side.Add(table.Key, dependency);
        }

        return dependency;
    }

    /// <summary>
    /// The typed relations of this script: the EXACT DeltaForge extract_typed_relations algorithm
    /// (types.rs). Outbound entities classify by LIFECYCLE over their combined operation set, never per
    /// operation: drop+create+data = Refreshed (Writes only: the schema re-definition is incidental);
    /// create = Created (Creates, plus Writes when data operations exist); drop alone = Destroyed (Destroys
    /// only, even if the script also inserted); data/truncate = Written (Writes); alter alone = Modified
    /// (Creates, the structural-source role). Inbound entities prune self-referential reads (read after own
    /// data write, the validation pattern), then data reads become Reads and structural references become
    /// Requires. Script-created entities that were also read emit Reads so internal chains stay visible,
    /// and every Writes without a Creates derives a Requires write-prerequisite (the entity must already
    /// exist, so the scheduler waits for its creator). One deliberate T-SQL deviation, documented: the
    /// reference also derives parent-container prerequisites (zone.schema) for created objects; SQL Server
    /// schemas are administrative, pre-existing containers, so that rule is not ported.
    /// </summary>
    public IReadOnlyList<(TableName Table, LineageRelation Relation)> TypedRelations()
    {
        var relations = new List<(TableName Table, LineageRelation Relation)>();
        var seen = new HashSet<(string, LineageRelation)>();

        void Emit(TableName table, LineageRelation relation)
        {
            if (seen.Add((table.Key, relation)))
            {
                relations.Add((table, relation));
            }
        }

        static bool HasAny(TableDependency dependency, params TableOperation[] operations)
            => operations.Any(dependency.Operations.Contains);

        // Outbound: lifecycle-driven classification over the combined operation set.
        foreach (var dependency in _outbound.Values.OrderBy(d => d.Table.Key, StringComparer.Ordinal))
        {
            var hasCreate = HasAny(dependency, TableOperation.Create, TableOperation.CreateAs);
            var hasDrop = HasAny(dependency, TableOperation.Drop);
            var hasDataWrite = HasAny(dependency,
                TableOperation.Insert, TableOperation.Update, TableOperation.Delete,
                TableOperation.Merge, TableOperation.BulkInsert);
            var hasTruncate = HasAny(dependency, TableOperation.Truncate);
            var hasSchemaChange = HasAny(dependency, TableOperation.Alter);

            if (hasDrop && hasCreate && hasDataWrite)
            {
                Emit(dependency.Table, LineageRelation.Writes); // Refreshed: full drop-and-rebuild loader.
            }
            else if (hasCreate || (!hasDrop && !hasDataWrite && !hasTruncate && hasSchemaChange))
            {
                // Created (or Modified): the structural source.
                Emit(dependency.Table, LineageRelation.Creates);
                if (hasDataWrite || hasTruncate)
                {
                    Emit(dependency.Table, LineageRelation.Writes);
                }
            }
            else if (hasDrop)
            {
                Emit(dependency.Table, LineageRelation.Destroys); // Destroyed: runs after all other users.
            }
            else if (hasDataWrite || hasTruncate)
            {
                Emit(dependency.Table, LineageRelation.Writes);
            }

            // else Maintained: invisible to the scheduler.
        }

        // Inbound: self-referential reads pruned, then data reads vs structural prerequisites.
        var selfWriteKeys = _outbound.Values
            .Where(d => HasAny(d, TableOperation.Insert, TableOperation.Merge, TableOperation.Update, TableOperation.Delete))
            .Select(d => d.Table.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var dependency in _inbound.Values.OrderBy(d => d.Table.Key, StringComparer.Ordinal))
        {
            if (selfWriteKeys.Contains(dependency.Table.Key))
            {
                continue;
            }

            Emit(dependency.Table,
                dependency.Operations.Contains(TableOperation.Read) ? LineageRelation.Reads : LineageRelation.Requires);
        }

        // Script-internal chains: a created entity the script also read stays visible as a Read (no
        // self-write pruning here, per the reference: the scheduler's own self-skip neutralizes it).
        foreach (var dependency in _inbound.Values.OrderBy(d => d.Table.Key, StringComparer.Ordinal))
        {
            if (ScriptCreated.Contains(dependency.Table.Key) && dependency.Operations.Contains(TableOperation.Read))
            {
                Emit(dependency.Table, LineageRelation.Reads);
            }
        }

        // Write prerequisites: writing an entity this script did not create requires it to exist.
        foreach (var (table, relation) in relations.Where(r => r.Relation == LineageRelation.Writes).ToList())
        {
            _ = relation;
            if (!seen.Contains((table.Key, LineageRelation.Creates)))
            {
                Emit(table, LineageRelation.Requires);
            }
        }

        return relations
            .OrderBy(r => r.Table.Key, StringComparer.Ordinal)
            .ThenBy(r => r.Relation)
            .ToList();
    }

    /// <summary>
    /// Phase two, the exact DeltaForge effective_inbound_tables algorithm: with no local-dep info, every
    /// inbound name; otherwise (1) BFS from every write target through <see cref="LocalDeps"/>, where a name
    /// WITH local deps dissolves into its sources and a terminal name is collected as a true source, plus
    /// (2) every inbound name not created by the script itself (standalone reads and existence
    /// prerequisites), with a fallback to the full inbound set if nothing resolved. Temp names never come
    /// back; names that only ever existed as CTE labels carry no identity and dissolve silently.
    /// </summary>
    public IReadOnlyList<TableName> EffectiveInbound()
    {
        if (LocalDeps.Count == 0)
        {
            return _inbound.Values.Select(d => d.Table).Where(t => !t.IsTemp)
                .OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        }

        var byKey = new Dictionary<string, TableName>(StringComparer.Ordinal);
        foreach (var dependency in _inbound.Values.Concat(_outbound.Values))
        {
            byKey.TryAdd(dependency.Table.Key, dependency.Table);
        }

        var resolved = new Dictionary<string, TableName>(StringComparer.Ordinal);

        // (1) BFS from every write target: intermediates dissolve, terminals are sources.
        foreach (var start in _outbound.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var key = queue.Dequeue();
                if (!visited.Add(key))
                {
                    continue;
                }

                if (LocalDeps.TryGetValue(key, out var sources))
                {
                    foreach (var source in sources)
                    {
                        queue.Enqueue(source);
                    }
                }
                else if (byKey.TryGetValue(key, out var table) && !table.IsTemp)
                {
                    // A terminal write target with no recorded movement collects as its own source, the
                    // DeltaForge behavior: an update-in-place flow thereby reads-depends on whichever flow
                    // loads that table, which is exactly the ordering truth.
                    resolved.TryAdd(key, table);
                }
            }
        }

        // (2) Standalone reads and prerequisites: every inbound the script did not itself create.
        foreach (var dependency in _inbound.Values)
        {
            if (!dependency.Table.IsTemp && !ScriptCreated.Contains(dependency.Table.Key))
            {
                resolved.TryAdd(dependency.Table.Key, dependency.Table);
            }
        }

        if (resolved.Count == 0)
        {
            // Nothing resolved: fall back rather than lose all lineage (the DeltaForge guard).
            return _inbound.Values.Select(d => d.Table).Where(t => !t.IsTemp)
                .OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        }

        return resolved.Values.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
    }
}
