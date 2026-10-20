namespace SqlFlow.Delivery.Order;

/// <summary>
/// The waves a repository's lineage put its flows in: each flow's wave (from 1), and the flows lineage could not order
/// (a cycle's fallback wave). A flow lineage left out has no wave.
/// </summary>
public sealed record RepositoryWaves(string Repository, IReadOnlyDictionary<string, int> Waves, IReadOnlySet<string> Unordered);

/// <summary>How a repository's waves fail the order.</summary>
public enum OrderBreachKind
{
    /// <summary>The flow delivering the referred records does not run in an earlier wave than the flow referring to them.</summary>
    NotEarlier,

    /// <summary>One of the two flows is in the fallback wave lineage puts the flows of a cycle in.</summary>
    Unordered,

    /// <summary>One of the two flows has no wave: lineage left it out (a document that does not load, an inactive pipeline).</summary>
    NoWave,
}

/// <summary>
/// A reference the order keeps between two flows of one repository that the repository's waves do not respect: records of
/// <see cref="Dependent"/> refer to records <see cref="Provider"/> delivers, and lineage does not run the provider first.
/// </summary>
public sealed record OrderBreach(OrderDelivery Dependent, OrderDelivery Provider, OrderEdge Edge, OrderBreachKind Kind, string Why);

/// <summary>
/// A reference the order keeps from a flow of the repository to records only other repositories deliver. Lineage orders the
/// flows of one repository, which are the flows its schedules run; the flows of another repository run in that
/// repository's schedules, which run on their own, so this is something to know, not something the waves keep.
/// </summary>
public sealed record CrossRepositoryDependency(OrderDelivery Dependent, string EntityType, IReadOnlyList<OrderDelivery> Providers);

/// <summary>What checking a repository's waves against a partition's order found.</summary>
public sealed record OrderCheckResult(IReadOnlyList<OrderBreach> Breaches, IReadOnlyList<CrossRepositoryDependency> CrossRepository)
{
    /// <summary>Whether the repository's waves keep every reference the order keeps between its own flows.</summary>
    public bool Holds => Breaches.Count == 0;
}

/// <summary>
/// Checks the waves of one repository against the order of a partition (osdu/docs/reference/concepts/osdu-order.md):
/// every reference the order keeps between two flows of the repository has the flow delivering the referred records in an
/// earlier wave, so a schedule running both runs the provider first, and every reference to records only other
/// repositories deliver is listed. Interfaces of one source are ordered by the source itself, so a reference between two
/// of them is not a breach.
/// </summary>
public static class OrderCheck
{
    public static OrderCheckResult Check(OrderPlan plan, RepositoryWaves waves)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(waves);
        var breaches = new List<OrderBreach>();
        var cross = new List<CrossRepositoryDependency>();
        var byFlow = new Dictionary<string, int>(waves.Waves, StringComparer.OrdinalIgnoreCase);
        var unordered = new HashSet<string>(waves.Unordered, StringComparer.OrdinalIgnoreCase);
        foreach (var dependent in plan.Deliveries.Where(d => SameRepository(d.Repository, waves.Repository)))
        {
            foreach (var type in plan.WaitsFor(dependent).Order(StringComparer.Ordinal))
            {
                var edge = plan.Edges.First(e => string.Equals(e.From, dependent.EntityType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.To, type, StringComparison.OrdinalIgnoreCase));
                var providers = plan.DeliveriesOf(type);
                var here = providers.Where(p => SameRepository(p.Repository, waves.Repository)).ToList();
                var elsewhere = providers.Where(p => !SameRepository(p.Repository, waves.Repository)).ToList();
                foreach (var provider in here.Where(p => !string.Equals(p.Flow, dependent.Flow, StringComparison.OrdinalIgnoreCase)))
                {
                    if (Breach(dependent, provider, edge, byFlow, unordered) is { } breach)
                    {
                        breaches.Add(breach);
                    }
                }

                if (elsewhere.Count > 0)
                {
                    cross.Add(new CrossRepositoryDependency(dependent, type, elsewhere));
                }
            }
        }

        return new OrderCheckResult(breaches, cross);
    }

    private static OrderBreach? Breach(OrderDelivery dependent, OrderDelivery provider, OrderEdge edge, Dictionary<string, int> waves, HashSet<string> unordered)
    {
        var what = $"{dependent.Label} delivers {dependent.EntityType}, which refers to {edge.To} ({string.Join(", ", edge.Because.Where(b => b.Delivery == dependent).Select(b => b.Property).Distinct(StringComparer.Ordinal))})";
        if (unordered.Contains(dependent.Flow) || unordered.Contains(provider.Flow))
        {
            var which = unordered.Contains(provider.Flow) ? provider.Flow : dependent.Flow;
            return new OrderBreach(dependent, provider, edge, OrderBreachKind.Unordered,
                $"{what} that {provider.Label} delivers, but lineage put {which} in the fallback wave of a cycle, so nothing runs {provider.Flow} first.");
        }

        if (!waves.TryGetValue(dependent.Flow, out var dependentWave) || !waves.TryGetValue(provider.Flow, out var providerWave))
        {
            var missing = waves.ContainsKey(dependent.Flow) ? provider.Flow : dependent.Flow;
            return new OrderBreach(dependent, provider, edge, OrderBreachKind.NoWave,
                $"{what} that {provider.Label} delivers, but lineage gave {missing} no wave, so nothing runs {provider.Flow} first.");
        }

        return providerWave < dependentWave
            ? null
            : new OrderBreach(dependent, provider, edge, OrderBreachKind.NotEarlier,
                $"{what} that {provider.Label} delivers, but lineage runs {provider.Flow} in wave {providerWave} and {dependent.Flow} in wave {dependentWave}.");
    }

    private static bool SameRepository(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
