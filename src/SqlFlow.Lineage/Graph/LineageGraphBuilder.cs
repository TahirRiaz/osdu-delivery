using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.Lineage.Graph;

/// <summary>
/// Phase three: merges the collected facts into the canonical graph and computes the execution plan with the
/// EXACT DeltaForge compute_schedule_run_order semantics: producer maps from typed relations
/// (creators/writers/readers per object), multi-creator warnings, edge-type-aware dependencies (Reads depends
/// on writers, skipping any writer that would create a mutual cycle, falling back to creators when no safe
/// writer exists; Requires depends on creators; Destroys depends on creators, writers, AND readers so nothing
/// is destroyed before its consumers ran; Creates/Writes imply nothing), modified Kahn's with max-level
/// assignment (levels are the concurrency waves), and cycle members traced into a warning path and placed in
/// a final fallback wave rather than refused. On top of that sits the V3 module expansion: a flow that reads
/// a view or executes a procedure inherits the module's derived relations transitively, so view chains and
/// proc bodies participate in ordering. Everything is ordinal-sorted: same input, same plan, byte for byte.
/// </summary>
public static class LineageGraphBuilder
{
    /// <summary>Module-inheritance ceiling: a deeper view-on-view chain is a modeling error, not lineage.</summary>
    public const int MaxModuleDepth = 32;

    public static LineageReport Build(
        CollectionResult collected, string flowDirectory, IReadOnlyList<LineageTier> tiersUsed, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(collected);
        ArgumentNullException.ThrowIfNull(tiersUsed);

        var warnings = new List<string>(collected.Warnings);

        // ---- Server identity aliases (proven at connect time) apply before anything else. -------------
        string Server(string serverRef)
            => collected.ServerAliases.TryGetValue(serverRef, out var canonical) ? canonical : serverRef;

        if (collected.ServerAliases.Count > 0)
        {
            collected = ApplyServerAliases(collected, Server);
        }

        // ---- Synonym resolution: every fact lands on the base object. -------------------------------
        var synonymTargets = collected.Synonyms.ToDictionary(
            s => NodeKey.For(s.ServerRef, s.Database, s.Schema, s.Name),
            s => (s.ServerRef, Database: s.TargetDatabase, Schema: s.TargetSchema, Name: s.TargetName),
            StringComparer.Ordinal);

        (string ServerRef, string? Database, string? Schema, string Name) ResolveSynonyms(
            string serverRef, string? database, string? schema, string name)
        {
            var current = (ServerRef: serverRef, Database: database, Schema: schema, Name: name);
            for (var hop = 0; hop < MaxModuleDepth; hop++)
            {
                if (!synonymTargets.TryGetValue(NodeKey.For(current.ServerRef, current.Database, current.Schema, current.Name), out var next))
                {
                    return current;
                }

                current = next;
            }

            warnings.Add($"synonym chain at '{serverRef}:{database}.{schema}.{name}' exceeds {MaxModuleDepth} hops (a synonym cycle); left unresolved.");
            return (serverRef, database, schema, name);
        }

        // ---- Identity unification BEFORE synonym resolution: a partially qualified fact must first gain
        // its full identity, otherwise a 2-part reference to a synonym never matches the synonym map and
        // its dependency edge silently vanishes.
        var aliasMap = BuildIdentityAliases(collected.Facts, collected.CatalogObjects, warnings);

        var facts = collected.Facts
            .Select(f =>
            {
                var key = NodeKey.For(f.ServerRef, f.Database, f.Schema, f.Name);
                if (aliasMap.TryGetValue(key, out var unified))
                {
                    f = f with
                    {
                        Database = unified.Database ?? f.Database,
                        Schema = unified.Schema ?? f.Schema,
                    };
                }

                var resolved = ResolveSynonyms(f.ServerRef, f.Database, f.Schema, f.Name);
                return f with { ServerRef = resolved.ServerRef, Database = resolved.Database, Schema = resolved.Schema, Name = resolved.Name };
            })
            .ToList();

        // The inventory and the module harvest can both describe one object (a procedure appears in
        // sys.objects AND, when encrypted, as a warning-bearing module entry): merge, never drop warnings.
        var catalogByKey = collected.CatalogObjects
            .GroupBy(o => NodeKey.For(o.ServerRef, o.Database, o.Schema, o.Name), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.First() with
                {
                    Kind = g.Select(o => o.Kind).FirstOrDefault(k => k != LineageNodeKind.Unknown, LineageNodeKind.Unknown),
                    Warning = g.Select(o => o.Warning).FirstOrDefault(w => w is not null),
                    // The inventory entry carries columns; the module-harvest entry carries the definition.
                    Definition = g.Select(o => o.Definition).FirstOrDefault(d => d is not null),
                    Columns = g.Select(o => o.Columns).FirstOrDefault(c => c.Count > 0) ?? [],
                },
                StringComparer.Ordinal);

        string KeyOf(LineageFact f) => NodeKey.For(f.ServerRef, f.Database, f.Schema, f.Name);

        var nodes = new Dictionary<string, LineageObjectNode>(StringComparer.Ordinal);
        void EnsureNode(string key, string serverRef, string? database, string? schema, string name, LineageNodeKind hint)
        {
            if (nodes.TryGetValue(key, out var existing))
            {
                if (existing.Kind == LineageNodeKind.Unknown && hint != LineageNodeKind.Unknown)
                {
                    nodes[key] = existing with { Kind = hint };
                }

                return;
            }

            // The key is the identity authority: a node reached through a unified or remapped key carries
            // the key's own database/schema, so metadata and key can never disagree.
            var keyParts = key.Split('|');
            var fromCatalog = catalogByKey.TryGetValue(key, out var catalogObject) ? catalogObject : null;
            var nodeWarnings = fromCatalog?.Warning is { } w ? new List<string> { w } : [];
            nodes[key] = new LineageObjectNode
            {
                Key = key,
                ServerRef = serverRef,
                Database = fromCatalog?.Database ?? (keyParts[1].Length > 0 ? keyParts[1] : database),
                Schema = fromCatalog?.Schema ?? (keyParts[2].Length > 0 ? keyParts[2] : schema),
                Name = name,
                Kind = fromCatalog?.Kind ?? hint,
                Definition = fromCatalog?.Definition,
                Columns = fromCatalog?.Columns ?? [],
                Warnings = nodeWarnings,
            };
        }

        foreach (var catalogObject in collected.CatalogObjects)
        {
            EnsureNode(
                NodeKey.For(catalogObject.ServerRef, catalogObject.Database, catalogObject.Schema, catalogObject.Name),
                catalogObject.ServerRef, catalogObject.Database, catalogObject.Schema, catalogObject.Name, catalogObject.Kind);
        }

        foreach (var fact in facts)
        {
            EnsureNode(KeyOf(fact), fact.ServerRef, fact.Database, fact.Schema, fact.Name, fact.KindHint);
        }

        // ---- Edge assembly: deduplicated, latest observation wins. -----------------------------------
        var edges = new Dictionary<(string? Flow, string? Module, LineageRelation Relation, string Key, LineageTier Tier), LineageEdge>();
        foreach (var fact in facts)
        {
            var key = KeyOf(fact);
            var moduleKey = fact.ViaModuleKey;
            var identity = (fact.Flow, moduleKey, fact.Relation, key, fact.Tier);
            if (!edges.TryGetValue(identity, out var existing) || fact.ObservedAtUtc > existing.ObservedAtUtc)
            {
                edges[identity] = new LineageEdge
                {
                    Flow = fact.Flow,
                    ViaModule = moduleKey,
                    Relation = fact.Relation,
                    ObjectKey = key,
                    Tier = fact.Tier,
                    ObservedRunId = fact.RunId,
                    ObservedAtUtc = fact.ObservedAtUtc,
                    Step = fact.Step,
                };
            }
        }

        // ---- Module relation inheritance. -------------------------------------------------------------
        // moduleKey -> the module's own relations (derived facts attributed via that module).
        var moduleRelations = facts
            .Where(f => f.ViaModuleKey is not null)
            .GroupBy(f => f.ViaModuleKey!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => (f.Relation, Key: KeyOf(f))).Distinct().OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Relation).ToList(),
                StringComparer.Ordinal);

        // ---- Per-flow effective relations (own facts plus transitive module inheritance). -------------
        // One name declared by several documents collapses to its first declaration (their facts merge under
        // it) and is warned about by the collector. The same grouping also yields the structured duplicate set
        // the report exposes, so a stricter consumer (batch membership) can refuse the collision without
        // parsing the warning text.
        var byName = collected.Flows
            .GroupBy(f => f.Node.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.First().Node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var flows = byName.Select(g => g.First()).ToList();
        var duplicateFlowNames = byName
            .Where(g => g.Count() > 1)
            .Select(g => new LineageDuplicateFlowName
            {
                Name = g.First().Node.Name,
                Files = g.Select(f => f.Node.File).OrderBy(file => file, StringComparer.Ordinal).ToList(),
            })
            .ToList();

        var effectiveRelations = new Dictionary<string, HashSet<(LineageRelation Relation, string Key)>>(StringComparer.OrdinalIgnoreCase);
        var flowFacts = facts.Where(f => f.Flow is not null).ToLookup(f => f.Flow!, StringComparer.OrdinalIgnoreCase);
        foreach (var flow in flows)
        {
            var relations = new HashSet<(LineageRelation, string)>();
            foreach (var fact in flowFacts[flow.Node.Name])
            {
                relations.Add((fact.Relation, KeyOf(fact)));
            }

            // A flow that EXECUTES a procedure is the parent of what that procedure does: attribute the proc's
            // derived reads/writes/creates to the flow as flow-attributed derived edges (in addition to the
            // module-attributed ones), so a table a stored-procedure flow builds traces back to the FLOW that
            // runs it - not only to the procedure module. 'relations' here is still the flow's own facts, so the
            // starting Requires references are the flow's direct proc executions.
            foreach (var (relation, key, viaModule) in InheritedProcedureEdges(relations, moduleRelations))
            {
                var identity = ((string?)flow.Node.Name, (string?)viaModule, relation, key, LineageTier.Derived);
                if (!edges.ContainsKey(identity))
                {
                    edges[identity] = new LineageEdge
                    {
                        Flow = flow.Node.Name,
                        ViaModule = viaModule,
                        Relation = relation,
                        ObjectKey = key,
                        Tier = LineageTier.Derived,
                    };
                }
            }

            InheritModuleRelations(relations, moduleRelations, warnings, flow.Node.Name);
            effectiveRelations[flow.Node.Name] = relations;
        }

        // ---- The DeltaForge schedule computation. ------------------------------------------------------
        var plan = ComputeRunOrder(flows, effectiveRelations, warnings, out var flowDependencies, out var cycles);

        return new LineageReport
        {
            GeneratedAtUtc = generatedAtUtc,
            FlowDirectory = flowDirectory,
            TiersUsed = tiersUsed,
            Flows = flows.Select(f => f.Node).ToList(),
            Objects = nodes.Values.OrderBy(n => n.Key, StringComparer.Ordinal).ToList(),
            Edges = edges.Values
                .OrderBy(e => e.Flow ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.ViaModule ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.ObjectKey, StringComparer.Ordinal)
                .ThenBy(e => e.Relation)
                .ThenBy(e => e.Tier)
                .ToList(),
            FlowDependencies = flowDependencies,
            ExecutionPlan = plan,
            Cycles = cycles,
            DuplicateFlowNames = duplicateFlowNames,
            Warnings = warnings.Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>
    /// The data relations a flow inherits by EXECUTING a procedure, for flow-attributed edge emission (distinct
    /// from <see cref="InheritModuleRelations"/>, which folds every module reference into a flow's scheduling
    /// relations). Starts only from the flow's own <c>Requires</c> references to a module (a proc execution), so
    /// a flow that merely reads a view is unchanged; walks transitively through the modules that proc references
    /// (a proc reading a view inherits the view's reads), depth-guarded; and yields each read/write/create with
    /// the module it belongs to as provenance. The output tables of a stored-procedure flow therefore trace back
    /// to the flow, which is what a data-flow view needs.
    /// </summary>
    private static IEnumerable<(LineageRelation Relation, string Key, string ViaModule)> InheritedProcedureEdges(
        IEnumerable<(LineageRelation Relation, string Key)> ownRelations,
        IReadOnlyDictionary<string, List<(LineageRelation Relation, string Key)>> moduleRelations)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Key, int Depth)>();
        foreach (var (relation, key) in ownRelations)
        {
            if (relation == LineageRelation.Requires && moduleRelations.ContainsKey(key))
            {
                queue.Enqueue((key, 1));
            }
        }

        while (queue.Count > 0)
        {
            var (moduleKey, depth) = queue.Dequeue();
            if (!visited.Add(moduleKey) || depth > MaxModuleDepth || !moduleRelations.TryGetValue(moduleKey, out var inherited))
            {
                continue;
            }

            foreach (var (relation, key) in inherited)
            {
                if (relation is LineageRelation.Reads or LineageRelation.Writes or LineageRelation.Creates)
                {
                    yield return (relation, key, moduleKey);
                }

                // Follow the proc's own reads/requires into further modules (a proc reading a view, or calling
                // another proc), so a table two hops down still traces to the executing flow.
                if (relation is LineageRelation.Reads or LineageRelation.Requires)
                {
                    queue.Enqueue((key, depth + 1));
                }
            }
        }
    }

    /// <summary>A flow reading a view (or requiring a procedure) inherits the module's derived relations,
    /// transitively through module-on-module references, depth-guarded: the expansion that connects a flow
    /// reading dbo.vw_Orders to the flow loading dbo.Orders.</summary>
    private static void InheritModuleRelations(
        HashSet<(LineageRelation Relation, string Key)> relations,
        IReadOnlyDictionary<string, List<(LineageRelation Relation, string Key)>> moduleRelations,
        List<string> warnings,
        string flowName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Key, int Depth)>();
        foreach (var (relation, key) in relations.Where(r => r.Relation is LineageRelation.Reads or LineageRelation.Requires).ToList())
        {
            _ = relation;
            queue.Enqueue((key, 1));
        }

        while (queue.Count > 0)
        {
            var (moduleKey, depth) = queue.Dequeue();
            if (!visited.Add(moduleKey) || !moduleRelations.TryGetValue(moduleKey, out var inherited))
            {
                continue;
            }

            if (depth > MaxModuleDepth)
            {
                warnings.Add($"flow '{flowName}': module expansion beyond {MaxModuleDepth} levels at '{moduleKey}'; deeper lineage is not inherited.");
                continue;
            }

            foreach (var relation in inherited)
            {
                relations.Add(relation);
                if (relation.Relation is LineageRelation.Reads or LineageRelation.Requires)
                {
                    queue.Enqueue((relation.Key, depth + 1));
                }
            }
        }
    }

    /// <summary>The exact compute_schedule_run_order port; see the class summary for the rules.</summary>
    private static LineageExecutionPlan ComputeRunOrder(
        IReadOnlyList<CollectedFlow> flows,
        IReadOnlyDictionary<string, HashSet<(LineageRelation Relation, string Key)>> effectiveRelations,
        List<string> warnings,
        out IReadOnlyList<LineageFlowDependency> flowDependencies,
        out IReadOnlyList<LineageCycle> cycles)
    {
        var n = flows.Count;
        var names = flows.Select(f => f.Node.Name).ToList();
        var relations = names.Select(name => effectiveRelations[name]).ToList();

        if (n == 0)
        {
            flowDependencies = [];
            cycles = [];
            return new LineageExecutionPlan { Waves = [], Unordered = [] };
        }

        // Producer maps: object -> flow indices, in flow order (deterministic).
        var creators = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var writers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var readers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            foreach (var (relation, key) in relations[i])
            {
                var map = relation switch
                {
                    LineageRelation.Creates => creators,
                    LineageRelation.Writes => writers,
                    LineageRelation.Reads => readers,
                    _ => null,
                };
                if (map is not null)
                {
                    (map.TryGetValue(key, out var list) ? list : map[key] = []).Add(i);
                }
            }
        }

        foreach (var (key, creatorIndices) in creators.Where(c => c.Value.Count > 1).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            warnings.Add($"object '{key}' is created by multiple flows: {string.Join(", ", creatorIndices.Select(i => names[i]))}.");
        }

        // Dependency edges: deps[i] = the flows i must wait for, with the mediating objects.
        var deps = new SortedSet<int>[n];
        var via = new Dictionary<(int From, int To), SortedSet<string>>();
        for (var i = 0; i < n; i++)
        {
            deps[i] = [];
        }

        void AddDep(int i, int j, string objectKey)
        {
            if (i == j)
            {
                return;
            }

            deps[i].Add(j);
            if (!via.TryGetValue((j, i), out var objects))
            {
                via[(j, i)] = objects = [];
            }

            objects.Add(objectKey);
        }

        for (var i = 0; i < n; i++)
        {
            var myWrites = relations[i].Where(r => r.Relation == LineageRelation.Writes).Select(r => r.Key)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (relation, key) in relations[i].OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Relation))
            {
                switch (relation)
                {
                    case LineageRelation.Reads:
                    {
                        // Depend on writers, skipping any writer that reads something I write (the mutual
                        // pair would otherwise deadlock the plan); fall back to creators when no safe
                        // writer exists.
                        var foundWriter = false;
                        if (writers.TryGetValue(key, out var writerIndices))
                        {
                            foreach (var j in writerIndices)
                            {
                                if (j == i)
                                {
                                    continue;
                                }

                                var jReads = relations[j].Where(r => r.Relation == LineageRelation.Reads).Select(r => r.Key);
                                if (myWrites.Count > 0 && jReads.Any(myWrites.Contains))
                                {
                                    continue;
                                }

                                AddDep(i, j, key);
                                foundWriter = true;
                            }
                        }

                        if (!foundWriter && creators.TryGetValue(key, out var creatorIndices))
                        {
                            foreach (var j in creatorIndices)
                            {
                                AddDep(i, j, key);
                            }
                        }

                        break;
                    }

                    case LineageRelation.Requires:
                        if (creators.TryGetValue(key, out var requiredCreators))
                        {
                            foreach (var j in requiredCreators)
                            {
                                AddDep(i, j, key);
                            }
                        }

                        break;

                    case LineageRelation.Destroys:
                        // Destruction goes last: wait for everyone who creates, writes, or reads the object.
                        foreach (var map in new[] { creators, writers, readers })
                        {
                            if (map.TryGetValue(key, out var indices))
                            {
                                foreach (var j in indices)
                                {
                                    AddDep(i, j, key);
                                }
                            }
                        }

                        break;

                    // Creates/Writes: no implicit dependency, the DeltaForge rule.
                }
            }
        }

        // Modified Kahn's with max-level assignment: levels are the waves.
        var inDegree = deps.Select(d => d.Count).ToArray();
        var forward = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            forward[i] = [];
        }

        for (var i = 0; i < n; i++)
        {
            foreach (var j in deps[i])
            {
                forward[j].Add(i);
            }
        }

        var level = new int[n];
        var processed = new HashSet<int>();
        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => inDegree[i] == 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            processed.Add(current);
            foreach (var dependent in forward[current])
            {
                level[dependent] = Math.Max(level[dependent], level[current] + 1);
                if (--inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        // Cycle handling: trace a path for the warning, then place the members in a fallback wave.
        var cycleList = new List<LineageCycle>();
        var unordered = new List<string>();
        if (processed.Count < n)
        {
            var unprocessed = Enumerable.Range(0, n).Where(i => !processed.Contains(i)).ToList();
            var path = TraceCycle(unprocessed, deps, names);
            warnings.Add($"dependency cycle detected: {string.Join(" -> ", path)}.");
            warnings.Add($"affected flows placed in the fallback wave: {string.Join(", ", unprocessed.Select(i => names[i]))}.");

            // The traced path runs dependent -> dependency: path[p] waits for path[p+1], whose edge was
            // recorded as (From: dependency, To: dependent). Only the path's own edges contribute.
            var viaObjects = new SortedSet<string>(StringComparer.Ordinal);
            for (var p = 0; p + 1 < path.Count; p++)
            {
                var dependency = names.IndexOf(path[p + 1]);
                var dependent = names.IndexOf(path[p]);
                if (dependency >= 0 && dependent >= 0 && via.TryGetValue((dependency, dependent), out var mediating))
                {
                    viaObjects.UnionWith(mediating);
                }
            }

            cycleList.Add(new LineageCycle { Flows = path, ViaObjects = viaObjects.ToList() });

            var fallbackLevel = (processed.Count > 0 ? processed.Max(i => level[i]) : 0) + 1;
            foreach (var i in unprocessed)
            {
                level[i] = fallbackLevel;
            }

            unordered.AddRange(unprocessed.Select(i => names[i]).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        }

        flowDependencies = via
            .Select(pair => new LineageFlowDependency
            {
                FromFlow = names[pair.Key.From],
                ToFlow = names[pair.Key.To],
                ViaObjects = pair.Value.ToList(),
            })
            .OrderBy(d => d.FromFlow, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.ToFlow, StringComparer.OrdinalIgnoreCase)
            .ToList();
        cycles = cycleList;

        var waves = Enumerable.Range(0, n)
            .GroupBy(i => level[i])
            .OrderBy(g => g.Key)
            .Select((group, index) => new LineageWave
            {
                Wave = index + 1,
                Flows = group.Select(i => names[i]).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .ToList();

        return new LineageExecutionPlan { Waves = waves, Unordered = unordered };
    }

    /// <summary>The DeltaForge trace_cycle: follow dependencies within the unprocessed set until a node
    /// repeats; the returned path ends on the repeated node. Deterministic: smallest index first.</summary>
    private static List<string> TraceCycle(IReadOnlyList<int> unprocessed, SortedSet<int>[] deps, IReadOnlyList<string> names)
    {
        var unprocessedSet = unprocessed.ToHashSet();
        var visited = new List<int>();
        var visitedSet = new HashSet<int>();
        var current = unprocessed.Min();

        while (true)
        {
            if (visitedSet.Contains(current))
            {
                var start = visited.IndexOf(current);
                var cycle = visited.Skip(start).Select(i => names[i]).ToList();
                cycle.Add(names[current]);
                return cycle;
            }

            visited.Add(current);
            visitedSet.Add(current);

            var next = deps[current].Where(unprocessedSet.Contains).Cast<int?>().FirstOrDefault();
            if (next is null)
            {
                return visited.Select(i => names[i]).ToList();
            }

            current = next.Value;
        }
    }

    /// <summary>Rewrites every collected element onto the canonical server identities proven at connect
    /// time, so one physical server referenced under several spellings is one graph node space.</summary>
    private static CollectionResult ApplyServerAliases(CollectionResult collected, Func<string, string> server)
    {
        var remapped = new CollectionResult();
        remapped.Flows.AddRange(collected.Flows.Select(f => f with
        {
            TargetServerRef = server(f.TargetServerRef),
            SourceServerRef = f.SourceServerRef is { } source ? server(source) : null,
        }));
        remapped.Facts.AddRange(collected.Facts.Select(f => f with { ServerRef = server(f.ServerRef) }));
        remapped.CatalogObjects.AddRange(collected.CatalogObjects.Select(o => o with { ServerRef = server(o.ServerRef) }));
        remapped.Synonyms.AddRange(collected.Synonyms.Select(s => s with { ServerRef = server(s.ServerRef) }));
        remapped.Warnings.AddRange(collected.Warnings);
        foreach (var (key, value) in collected.Servers)
        {
            remapped.Servers.TryAdd(server(key), value);
        }

        return remapped;
    }

    /// <summary>
    /// Identity unification: a key missing its database (or schema) maps onto the one catalog/fact identity
    /// that matches its remaining parts, when exactly one exists; the alias carries the original-cased
    /// missing parts so node metadata never degrades to key casing. With several candidates the key stays
    /// split and a warning names the ambiguity: a wrong merge would silently corrupt the execution order.
    /// </summary>
    private static Dictionary<string, (string? Database, string? Schema)> BuildIdentityAliases(
        IReadOnlyList<LineageFact> facts, IReadOnlyList<CatalogObject> catalogObjects, List<string> warnings)
    {
        var aliases = new Dictionary<string, (string? Database, string? Schema)>(StringComparer.Ordinal);

        var full = facts
            .Where(f => f.Database is not null && f.Schema is not null)
            .Select(f => (f.ServerRef, f.Database, f.Schema, f.Name))
            .Concat(catalogObjects.Select(o => (o.ServerRef, (string?)o.Database, (string?)o.Schema, o.Name)))
            .Distinct()
            .ToList();

        var byServerSchemaName = full
            .GroupBy(x => NodeKey.For(x.Item1, null, x.Item3, x.Item4), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Distinct().ToList(), StringComparer.Ordinal);
        var byServerDbName = full
            .GroupBy(x => NodeKey.For(x.Item1, x.Item2, null, x.Item4), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Distinct().ToList(), StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            if (fact.Database is null && fact.Schema is not null)
            {
                var key = NodeKey.For(fact.ServerRef, null, fact.Schema, fact.Name);
                if (aliases.ContainsKey(key) || !byServerSchemaName.TryGetValue(key, out var candidates))
                {
                    continue;
                }

                var databases = candidates.Select(c => c.Item2!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (databases.Count == 1)
                {
                    aliases[key] = (databases[0], null);
                }
                else if (databases.Count > 1)
                {
                    warnings.Add(
                        $"'{fact.Schema}.{fact.Name}' on '{fact.ServerRef}' matches {databases.Count} databases ({string.Join(", ", databases.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))}); left unresolved.");
                }
            }
            else if (fact.Database is not null && fact.Schema is null)
            {
                var key = NodeKey.For(fact.ServerRef, fact.Database, null, fact.Name);
                if (aliases.ContainsKey(key) || !byServerDbName.TryGetValue(key, out var candidates))
                {
                    continue;
                }

                var schemas = candidates.Select(c => c.Item3!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (schemas.Count == 1)
                {
                    aliases[key] = (null, schemas[0]);
                }
                else if (schemas.Count > 1)
                {
                    warnings.Add(
                        $"'{fact.Database}..{fact.Name}' on '{fact.ServerRef}' matches {schemas.Count} schemas ({string.Join(", ", schemas.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}); left unresolved.");
                }
            }
        }

        return aliases;
    }
}
