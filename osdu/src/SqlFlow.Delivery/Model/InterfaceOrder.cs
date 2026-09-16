namespace SqlFlow.Delivery.Model;

/// <summary>Why one interface of a source waits for another: <paramref name="Interface"/> runs after <paramref name="DependsOn"/>.</summary>
public sealed record InterfaceDependency(string Interface, string DependsOn, string Why);

/// <summary>
/// The order a source's interfaces run in (docs/interfaces-design.md section 6): waves, each holding the interfaces whose
/// dependencies all belong to earlier waves, in document order within a wave. Interface names are compared ignoring case,
/// as the ledger identities derived from them are.
/// </summary>
public static class InterfaceOrder
{
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
    /// The waves <paramref name="interfaces"/> run in. A dependency on an interface outside the list is not waited for: a
    /// run of some interfaces reads what the others already delivered from the ledger as it is. A cycle among the listed
    /// interfaces is refused with the interfaces around it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Waves(IReadOnlyList<string> interfaces, IEnumerable<InterfaceDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(dependencies);
        var edges = Edges(interfaces, dependencies);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var waves = new List<IReadOnlyList<string>>();
        while (placed.Count < interfaces.Count)
        {
            var wave = interfaces
                .Where(i => !placed.Contains(i) && edges[i].All(placed.Contains))
                .ToList();
            if (wave.Count == 0)
            {
                var cycle = Cycle(interfaces, dependencies) ?? interfaces.Where(i => !placed.Contains(i)).ToList();
                throw new DeliveryException($"The interfaces {Describe(cycle)} wait for each other, so none of them can run first.");
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

    private static string Describe(IReadOnlyList<string> names) => string.Join(" -> ", names);
}
