using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>The three ways a run can be scoped, the V3 equivalent of the legacy Flow / Node / Batch executions.</summary>
public enum RunScope
{
    /// <summary>One flow (the default single-flow trigger).</summary>
    Flow,

    /// <summary>A flow and all of its transitive descendants (legacy "Node").</summary>
    Node,

    /// <summary>Every active flow in one batch / data source (legacy "Batch").</summary>
    Batch,
}

/// <summary>The stored spelling of a <see cref="RunScope"/>: the persisted vocabulary a schedule's scope column and
/// the YAML <c>scope:</c> key share, so the string form lives in exactly one place. Parse with
/// <see cref="RunScopeExpander.TryParseScope"/>.</summary>
public static class RunScopes
{
    /// <summary>One flow: the schedule's own flow, enqueued as a single run.</summary>
    public const string Flow = "flow";

    /// <summary>A flow and all of its transitive descendants.</summary>
    public const string Node = "node";

    /// <summary>Every active flow in one batch / data source.</summary>
    public const string Batch = "batch";

    /// <summary>The stored spelling of a scope.</summary>
    public static string From(RunScope scope) => scope switch
    {
        RunScope.Node => Node,
        RunScope.Batch => Batch,
        _ => Flow,
    };
}

/// <summary>One flow selected by a scope expansion, with the wave that orders it within the set.</summary>
public sealed record RunScopeMember(string FlowName, string FlowKind, int Wave);

/// <summary>The result of expanding a scope: what the set was anchored on (a flow name for Node, a batch label for
/// Batch, the flow name for Flow) and the ordered member flows.</summary>
public sealed record RunScopeExpansion(RunScope Scope, string Anchor, IReadOnlyList<RunScopeMember> Members);

/// <summary>
/// Turns a run scope (Flow / Node / Batch) into the concrete, ordered set of flows to enqueue. This is the single
/// place that answers "which flows does this execution touch", shared by the read-only preview endpoint and the
/// trigger endpoint so the two can never disagree. It reads exactly the data the lineage graph does: the flow-to-flow
/// dependency edges (<see cref="CatalogFlowDependency"/>) for descendants, the batch label for a batch, and the
/// topological wave (<see cref="CatalogPipeline.Wave"/>) for ordering. Only active pipelines are ever selected, so a
/// flow that has left the estate is never enqueued; a <c>mode: manual</c> pipeline is likewise excluded from group
/// membership (a Node's anchor is the one exception: naming it IS the manual trigger). Stateless, like the rest of
/// the catalog stores.
/// </summary>
public static class RunScopeExpander
{
    /// <summary>Parses a scope string from the API (case-insensitive), or null when it is not a known scope.</summary>
    public static RunScope? TryParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "flow" or "" or null => RunScope.Flow,
        "node" => RunScope.Node,
        "batch" => RunScope.Batch,
        _ => null,
    };

    /// <summary>
    /// Expands <paramref name="scope"/> into its ordered member flows. For Flow/Node the <paramref name="anchorFlow"/>
    /// identifies the starting flow; for Batch either <paramref name="batch"/> names the batch directly, or the
    /// anchor flow's own batch is used. The returned members are active pipelines ordered by wave then name; a wave
    /// that lineage has not computed yet (-1) collapses to 0 so an un-analyzed set runs as a single parallel wave
    /// rather than in an undefined order.
    /// </summary>
    public static async Task<RunScopeExpansion> ExpandAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, RunScope scope, string? batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return scope switch
        {
            RunScope.Flow => await ExpandFlowAsync(catalog, repoId, anchorFlow, ct).ConfigureAwait(false),
            RunScope.Node => await ExpandNodeAsync(catalog, repoId, anchorFlow, ct).ConfigureAwait(false),
            RunScope.Batch => await ExpandBatchAsync(catalog, repoId, anchorFlow, batch, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown run scope."),
        };
    }

    private static async Task<RunScopeExpansion> ExpandFlowAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, CancellationToken ct)
    {
        var flowName = RequireAnchor(anchorFlow);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var member = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.RepoId == repoId && p.Active)
            .Select(p => new RunScopeMember(p.Name, p.Kind, p.Wave < 0 ? 0 : p.Wave))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var members = member is null ? Array.Empty<RunScopeMember>() : new[] { member };
        return new RunScopeExpansion(RunScope.Flow, flowName, members);
    }

    private static async Task<RunScopeExpansion> ExpandNodeAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, CancellationToken ct)
    {
        var flowName = RequireAnchor(anchorFlow);
        var anchorId = CatalogIdentity.Pipeline(repoId, flowName);

        // The anchor must itself be an active flow; if it left the estate there is nothing to run.
        var anchorActive = await catalog.Pipelines.AsNoTracking()
            .AnyAsync(p => p.Id == anchorId && p.RepoId == repoId && p.Active, ct).ConfigureAwait(false);
        if (!anchorActive)
        {
            return new RunScopeExpansion(RunScope.Node, flowName, Array.Empty<RunScopeMember>());
        }

        // The flow-to-flow dependency edges for this repo: From must finish before To. Reachability from the anchor
        // over From -> To is exactly its transitive descendant set (the same closure the lineage graph highlights).
        var edges = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId)
            .Select(d => new { d.FromPipelineId, d.ToPipelineId })
            .ToListAsync(ct).ConfigureAwait(false);

        var outgoing = new Dictionary<Guid, List<Guid>>();
        foreach (var edge in edges)
        {
            if (!outgoing.TryGetValue(edge.FromPipelineId, out var to))
            {
                to = new List<Guid>();
                outgoing[edge.FromPipelineId] = to;
            }

            to.Add(edge.ToPipelineId);
        }

        var reachable = new HashSet<Guid> { anchorId };
        var queue = new Queue<Guid>();
        queue.Enqueue(anchorId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoing.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var next in neighbors)
            {
                if (reachable.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        // Manual-mode descendants are excluded (a group is automatic execution); the anchor itself is kept even
        // when manual, because the caller named it explicitly and a direct request IS the manual trigger.
        var members = await MembersByIdAsync(catalog, repoId, reachable, anchorId, ct).ConfigureAwait(false);
        return new RunScopeExpansion(RunScope.Node, flowName, members);
    }

    private static async Task<RunScopeExpansion> ExpandBatchAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, string? batch, CancellationToken ct)
    {
        // The batch label is taken directly when supplied, otherwise from the anchor flow's own batch (coalesced to
        // the default label exactly as every batch-grouping surface does).
        var label = batch?.Trim();
        if (string.IsNullOrEmpty(label))
        {
            var flowName = RequireAnchor(anchorFlow);
            var anchorId = CatalogIdentity.Pipeline(repoId, flowName);
            label = await catalog.Pipelines.AsNoTracking()
                .Where(p => p.Id == anchorId && p.RepoId == repoId)
                .Select(p => p.Batch)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            label = string.IsNullOrWhiteSpace(label) ? CatalogPipeline.DefaultBatch : label;
        }

        // Manual-mode flows never join a batch execution: their own document reserved them for a direct trigger.
        var members = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active
                        && p.ExecutionMode != PipelineExecutionModes.Manual
                        && (p.Batch ?? CatalogPipeline.DefaultBatch) == label)
            .OrderBy(p => p.Wave < 0 ? 0 : p.Wave).ThenBy(p => p.Name)
            .Select(p => new RunScopeMember(p.Name, p.Kind, p.Wave < 0 ? 0 : p.Wave))
            .ToListAsync(ct).ConfigureAwait(false);
        return new RunScopeExpansion(RunScope.Batch, label!, members);
    }

    private static async Task<IReadOnlyList<RunScopeMember>> MembersByIdAsync(
        CatalogDbContext catalog, Guid repoId, IReadOnlyCollection<Guid> pipelineIds, Guid anchorId, CancellationToken ct)
    {
        if (pipelineIds.Count == 0)
        {
            return Array.Empty<RunScopeMember>();
        }

        var ids = pipelineIds.ToList();
        return await catalog.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active && ids.Contains(p.Id)
                        && (p.Id == anchorId || p.ExecutionMode != PipelineExecutionModes.Manual))
            .OrderBy(p => p.Wave < 0 ? 0 : p.Wave).ThenBy(p => p.Name)
            .Select(p => new RunScopeMember(p.Name, p.Kind, p.Wave < 0 ? 0 : p.Wave))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static string RequireAnchor(string? anchorFlow)
    {
        var trimmed = anchorFlow?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("A flow-scoped or node-scoped run requires an anchor flow name.", nameof(anchorFlow));
        }

        return trimmed;
    }
}
