using SqlFlow.Delivery.Documents;

namespace SqlFlow.Delivery.Model;

/// <summary>Where a dependency between two interfaces of a source comes from.</summary>
public enum DependencyOrigin
{
    /// <summary>The document declares it with <c>after:</c>.</summary>
    After,

    /// <summary>A property the interface's mapping fills refers to the kind the other interface delivers.</summary>
    Schema,
}

/// <summary>Why one interface of a source waits for another: <paramref name="Interface"/> runs after <paramref name="DependsOn"/>.</summary>
public sealed record InterfaceDependency(string Interface, string DependsOn, string Why)
{
    /// <summary>Whether the document declares the dependency or a relationship the mapping fills implies it.</summary>
    public DependencyOrigin Origin { get; init; } = DependencyOrigin.After;
}

/// <summary>
/// A property an interface's mapping fills whose schema declares that it refers to other records: the entity types it may
/// point to (<c>master-data--Wellbore</c>), or a group type alone (<c>dataset</c>) when any kind of the group will do.
/// </summary>
public sealed record InterfaceReference(string Property, IReadOnlyList<string> Targets);

/// <summary>What an interface delivers, and what the records it renders refer to: the kind its mapping fills, and every filled property whose schema declares a relationship.</summary>
public sealed record InterfaceSchema(string Interface, string Kind, IReadOnlyList<InterfaceReference> References)
{
    /// <summary>The kind's entity type (<c>master-data--Wellbore</c>), or null when the kind names none.</summary>
    public string? EntityType => OsduKind.EntityType(Kind);

    /// <summary>The kind's group type (<c>master-data</c>).</summary>
    public string GroupType => OsduKind.Group(Kind);
}

/// <summary>
/// The order a run takes a source's interfaces in: the waves, what each interface waits for and why, and the references
/// that are not waited for because the interfaces on both sides refer to each other.
/// </summary>
public sealed record InterfaceOrderPlan(
    IReadOnlyList<IReadOnlyList<string>> Waves,
    IReadOnlyList<InterfaceDependency> Dependencies,
    IReadOnlyList<InterfaceDependency> NotWaitedFor)
{
    /// <summary>The wave <paramref name="interfaceName"/> runs in, from 1.</summary>
    public int WaveOf(string interfaceName)
    {
        ArgumentNullException.ThrowIfNull(interfaceName);
        for (var i = 0; i < Waves.Count; i++)
        {
            if (Waves[i].Contains(interfaceName, StringComparer.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        throw new ArgumentException($"'{interfaceName}' is not an interface of this order.", nameof(interfaceName));
    }

    /// <summary>What <paramref name="interfaceName"/> waits for, with why.</summary>
    public IReadOnlyList<InterfaceDependency> WaitsFor(string interfaceName)
        => Dependencies.Where(d => string.Equals(d.Interface, interfaceName, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The waves as text: the interfaces of a wave joined by '+', the waves by "then".</summary>
    public string Describe() => string.Join(" then ", Waves.Select(w => string.Join(" + ", w)));
}

/// <summary>
/// The order a source's interfaces run in (docs/interfaces-design.md section 6). An interface waits for the interfaces
/// <c>after:</c> names, and for every interface delivering a kind that a property its mapping fills refers to, as the
/// property's <c>x-osdu-relationship</c> declares it in the schema. The interfaces run in waves: each wave holds the
/// interfaces whose dependencies all ran in earlier waves, ordered by <see cref="GroupRank"/> and then as the document
/// lists them. Interface names are compared ignoring case, as the ledger identities derived from them are.
/// <para>OSDU's schemas refer both ways (a wellbore to its definitive trajectory, the trajectory to its wellbore), so
/// the references can wait for each other. Such a cycle is cut: first a reference the document's <c>after:</c> orders
/// the other way, then a reference from a group that ranks earlier to one that ranks later, which points back and
/// resolves once the other side has landed. A cycle neither cuts is refused, naming the interfaces.</para>
/// </summary>
public static class InterfaceOrder
{
    /// <summary>
    /// The group types in the direction OSDU's schemas refer: everything refers to reference data; work product components
    /// refer to master data and to their datasets (AbstractWPCGroupType, Datasets[]); a work product lists its components
    /// (WorkProduct, Components[]); datasets refer to reference data alone (AbstractDataset).
    /// </summary>
    private static readonly string[] Groups = ["reference-data", "master-data", "dataset", "work-product-component", "work-product"];

    /// <summary>
    /// Where a group type stands in <see cref="Groups"/>: a lower rank runs first when a cycle is cut or a wave is ordered.
    /// A group the list does not name ranks after all of them.
    /// </summary>
    public static int GroupRank(string groupType)
    {
        ArgumentNullException.ThrowIfNull(groupType);
        var rank = Array.FindIndex(Groups, g => string.Equals(g, groupType, StringComparison.OrdinalIgnoreCase));
        return rank < 0 ? Groups.Length : rank;
    }

    /// <summary>The dependencies the document declares with <c>after:</c>.</summary>
    public static IReadOnlyList<InterfaceDependency> Declared(SourceDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Interfaces
            .Where(i => i.Interface is not null)
            .SelectMany(i => i.After.Select(after => new InterfaceDependency(i.Interface!, after, $"interfaces.{i.Interface}.after names it")))
            .ToList();
    }

    /// <summary>
    /// The dependencies the schemas imply: an interface waits for every other interface delivering a kind that a property
    /// its mapping fills refers to. A reference to a kind no other interface delivers is not waited for: those records are
    /// OSDU's, or another source's.
    /// </summary>
    public static IReadOnlyList<InterfaceDependency> FromSchemas(IReadOnlyList<InterfaceSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var result = new List<InterfaceDependency>();
        foreach (var schema in schemas)
        {
            foreach (var other in schemas)
            {
                if (string.Equals(schema.Interface, other.Interface, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var reasons = schema.References
                    .SelectMany(r => r.Targets.Where(t => Refers(t, other)).Select(t => $"{r.Property} refers to {t}"))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (reasons.Count == 0)
                {
                    continue;
                }

                const int Shown = 3;
                var why = string.Join("; ", reasons.Take(Shown)) + (reasons.Count > Shown ? $"; and {reasons.Count - Shown} more" : string.Empty);
                result.Add(new InterfaceDependency(schema.Interface, other.Interface, $"{why}, which {other.Interface} delivers ({other.Kind})")
                {
                    Origin = DependencyOrigin.Schema,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// The order <paramref name="interfaces"/> run in, from what the document declares and what the schemas of
    /// <paramref name="schemas"/> imply. A dependency on an interface outside the list is not waited for: a run of some
    /// interfaces reads what the others already delivered from the ledger as it is. Throws
    /// <see cref="DeliveryException"/> for interfaces that wait for each other in a way no rule cuts.
    /// </summary>
    public static InterfaceOrderPlan Plan(IReadOnlyList<string> interfaces, IReadOnlyList<InterfaceDependency> declared, IReadOnlyList<InterfaceSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(schemas);
        var listed = new HashSet<string>(interfaces, StringComparer.OrdinalIgnoreCase);
        var ranks = schemas
            .Where(s => listed.Contains(s.Interface))
            .ToDictionary(s => s.Interface, s => GroupRank(s.GroupType), StringComparer.OrdinalIgnoreCase);
        int RankOf(string name) => ranks.TryGetValue(name, out var rank) ? rank : Groups.Length;

        // Every dependency names the interfaces as the list spells them, however after: wrote them.
        string Listed(string name) => interfaces.First(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase));
        var kept = declared
            .Where(d => listed.Contains(d.Interface) && listed.Contains(d.DependsOn))
            .Select(d => d with { Interface = Listed(d.Interface), DependsOn = Listed(d.DependsOn) })
            .ToList();
        foreach (var implied in FromSchemas(schemas.Where(s => listed.Contains(s.Interface)).ToList()))
        {
            if (!kept.Any(d => Same(d, implied)))
            {
                kept.Add(implied with { Interface = Listed(implied.Interface), DependsOn = Listed(implied.DependsOn) });
            }
        }

        var notWaitedFor = new List<InterfaceDependency>();
        while (Cycle(interfaces, kept) is { } cycle)
        {
            var around = new List<InterfaceDependency>();
            for (var i = 0; i + 1 < cycle.Count; i++)
            {
                around.Add(kept.First(d => Same(d, cycle[i], cycle[i + 1])));
            }

            var implied = around.Where(d => d.Origin == DependencyOrigin.Schema).ToList();
            var ordered = kept.Where(d => d.Origin == DependencyOrigin.After).ToList();
            var cut = implied
                .Where(d => Reaches(interfaces, ordered, d.DependsOn, d.Interface))
                .Select(d => d with { Why = $"{d.Why}; not waited for, because after: runs {d.DependsOn} after {d.Interface}" })
                .ToList();
            if (cut.Count == 0)
            {
                cut = implied
                    .Where(d => RankOf(d.Interface) < RankOf(d.DependsOn))
                    .Select(d => d with
                    {
                        Why = $"{d.Why}; not waited for, because {GroupOf(schemas, d.Interface)} runs before {GroupOf(schemas, d.DependsOn)}, and the reference resolves once {d.DependsOn} has delivered",
                    })
                    .ToList();
            }

            if (cut.Count == 0)
            {
                throw new DeliveryException(
                    $"The interfaces {string.Join(" -> ", cycle)} wait for each other ({string.Join("; ", around.Select(d => $"{d.Interface}: {d.Why}"))}), "
                    + "and neither after: nor their OSDU groups say which goes first. Name the interface that waits for the other with after:.");
            }

            foreach (var dependency in cut)
            {
                kept.RemoveAll(d => Same(d, dependency));
                notWaitedFor.Add(dependency);
            }
        }

        return new InterfaceOrderPlan(Waves(interfaces, kept, RankOf), kept, notWaitedFor);
    }

    /// <summary>
    /// The waves <paramref name="interfaces"/> run in, each ordered by <paramref name="rank"/> and then as listed. A
    /// dependency on an interface outside the list is not waited for. A cycle among the listed interfaces is refused with
    /// the interfaces around it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Waves(IReadOnlyList<string> interfaces, IEnumerable<InterfaceDependency> dependencies, Func<string, int>? rank = null)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(dependencies);
        var edges = Edges(interfaces, dependencies);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waves = new List<IReadOnlyList<string>>();
        while (placed.Count < interfaces.Count)
        {
            var wave = interfaces
                .Select((name, index) => (Name: name, Index: index))
                .Where(i => !placed.Contains(i.Name) && edges[i.Name].All(placed.Contains))
                .OrderBy(i => rank?.Invoke(i.Name) ?? 0)
                .ThenBy(i => i.Index)
                .Select(i => i.Name)
                .ToList();
            if (wave.Count == 0)
            {
                var cycle = Cycle(interfaces, dependencies) ?? interfaces.Where(i => !placed.Contains(i)).ToList();
                throw new DeliveryException($"The interfaces {string.Join(" -> ", cycle)} wait for each other, so none of them can run first.");
            }

            placed.UnionWith(wave);
            waves.Add(wave);
        }

        return waves;
    }

    /// <summary>A cycle among <paramref name="interfaces"/>, as the names around it with the first repeated at the end, or null.</summary>
    public static IReadOnlyList<string>? Cycle(IReadOnlyList<string> interfaces, IEnumerable<InterfaceDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(dependencies);
        var edges = Edges(interfaces, dependencies);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var start in interfaces)
        {
            if (state.GetValueOrDefault(start) != 0)
            {
                continue;
            }

            // An explicit stack, so a long chain of interfaces cannot exhaust the call stack.
            var stack = new Stack<(string Node, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            path.Add(start);
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var targets = edges[node];
                if (next < targets.Count)
                {
                    stack.Push((node, next + 1));
                    var target = targets[next];
                    switch (state.GetValueOrDefault(target))
                    {
                        case 0:
                            state[target] = 1;
                            path.Add(target);
                            stack.Push((target, 0));
                            break;
                        case 1:
                            var from = path.FindIndex(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase));
                            return [.. path.Skip(from), path[from]];
                    }

                    continue;
                }

                state[node] = 2;
                path.RemoveAt(path.Count - 1);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="target"/>, a relationship's entity type or group type, names what <paramref name="other"/> delivers.</summary>
    private static bool Refers(string target, InterfaceSchema other)
        => target.Contains("--", StringComparison.Ordinal)
            ? string.Equals(target, other.EntityType, StringComparison.OrdinalIgnoreCase)
            : string.Equals(target, other.GroupType, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="from"/> waits, through <paramref name="dependencies"/>, for <paramref name="to"/>.</summary>
    private static bool Reaches(IReadOnlyList<string> interfaces, IReadOnlyList<InterfaceDependency> dependencies, string from, string to)
    {
        var edges = Edges(interfaces, dependencies);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var next in edges[node])
            {
                if (string.Equals(next, to, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    private static string GroupOf(IReadOnlyList<InterfaceSchema> schemas, string interfaceName)
        => schemas.FirstOrDefault(s => string.Equals(s.Interface, interfaceName, StringComparison.OrdinalIgnoreCase))?.GroupType is { } group
            ? $"{group} ({interfaceName})"
            : interfaceName;

    private static bool Same(InterfaceDependency a, InterfaceDependency b) => Same(a, b.Interface, b.DependsOn);

    private static bool Same(InterfaceDependency dependency, string interfaceName, string dependsOn)
        => string.Equals(dependency.Interface, interfaceName, StringComparison.OrdinalIgnoreCase)
           && string.Equals(dependency.DependsOn, dependsOn, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every interface of the list with the listed interfaces it waits for, in document order.</summary>
    private static Dictionary<string, List<string>> Edges(IReadOnlyList<string> interfaces, IEnumerable<InterfaceDependency> dependencies)
    {
        var listed = new HashSet<string>(interfaces, StringComparer.OrdinalIgnoreCase);
        var edges = interfaces.ToDictionary(i => i, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            if (listed.Contains(dependency.Interface) && listed.Contains(dependency.DependsOn)
                && !edges[dependency.Interface].Contains(dependency.DependsOn, StringComparer.OrdinalIgnoreCase))
            {
                edges[dependency.Interface].Add(interfaces.First(i => string.Equals(i, dependency.DependsOn, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return edges;
    }
}
