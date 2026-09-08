using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>The ways a manual run can be scoped. A SCHEDULE is not scoped: it runs its member set, and membership
/// is the only selector (see <see cref="CatalogScheduleMember"/>).</summary>
public enum RunScope
{
    /// <summary>One flow (the single-flow trigger).</summary>
    Flow,
}

/// <summary>The stored spelling of a <see cref="RunScope"/>, so the string form lives in exactly one place. Parse
/// with <see cref="RunScopeExpander.TryParseScope"/>.</summary>
public static class RunScopes
{
    /// <summary>One flow, enqueued as a single run.</summary>
    public const string Flow = "flow";

    /// <summary>The stored spelling of a scope.</summary>
    public static string From(RunScope scope) => Flow;
}

/// <summary>One flow selected by a scope expansion, with the wave that orders it within the set and the batch it
/// carries (coalesced to <see cref="CatalogPipeline.DefaultBatch"/> when the flow declares none), so a caller can
/// group or filter the set by batch without a second lookup. <see cref="PipelineId"/> travels with the name so a
/// reader of the set can address the flow itself (its detail page, another lookup) instead of only naming it; the
/// expansions all read it off the pipeline row they already join. It is optional because the queue's own callers
/// construct members from a name alone.</summary>
public sealed record RunScopeMember(string FlowName, string FlowKind, int Wave, string Batch, Guid? PipelineId = null);

/// <summary>The result of expanding a set: what it was anchored on (the flow name for a flow, the schedule name
/// for a schedule's member set) and the ordered member flows.</summary>
public sealed record RunScopeExpansion(RunScope Scope, string Anchor, IReadOnlyList<RunScopeMember> Members);

/// <summary>
/// Turns a run scope into the concrete, ordered set of flows to enqueue. This is the single place that answers
/// "which flows does this execution touch", shared by the read-only preview endpoint and the trigger endpoint so
/// the two can never disagree. Only active pipelines are ever selected, so a flow that has left the estate is never
/// enqueued; a <c>mode: manual</c> or <c>mode: disabled</c> pipeline is likewise excluded from a schedule's member
/// set (naming a flow directly IS the manual trigger). Stateless, like the rest of the catalog stores.
/// </summary>
public static class RunScopeExpander
{
    /// <summary>Parses a scope string from the API (case-insensitive), or null when it is not a known scope.</summary>
    public static RunScope? TryParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "flow" or "" or null => RunScope.Flow,
        _ => null,
    };

    /// <summary>Expands <paramref name="scope"/> into its member flows: for a flow scope, the active pipeline named
    /// by <paramref name="anchorFlow"/>, or an empty set when it is inactive or unknown.</summary>
    public static async Task<RunScopeExpansion> ExpandAsync(
        CatalogDbContext catalog, Guid repoId, string? anchorFlow, RunScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (scope != RunScope.Flow)
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown run scope.");
        }

        var flowName = RequireAnchor(anchorFlow);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var member = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.RepoId == repoId && p.Active)
            .Select(p => new RunScopeMember(
                p.Name, p.Kind, p.Wave < 0 ? 0 : p.Wave, p.Batch ?? CatalogPipeline.DefaultBatch, p.Id))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var members = member is null ? Array.Empty<RunScopeMember>() : new[] { member };
        return new RunScopeExpansion(RunScope.Flow, flowName, members);
    }

    /// <summary>
    /// The member set of a schedule, in wave order: what one fire runs. Membership is the only selector, so this
    /// reads <see cref="CatalogScheduleMember"/> rather than matching any label. <paramref name="batchFilter"/>
    /// optionally narrows the set to members carrying one of those <c>batch:</c> tags, which is how "run the
    /// nightly, but only these flows" is expressed; it never widens the set, so a filter can only ever run a subset
    /// of what the schedule already owns. A null or empty filter runs every member. Inactive, <c>mode: manual</c>,
    /// and <c>mode: disabled</c> members are excluded: a manual flow reserved itself for a direct trigger, and a
    /// disabled one is deactivated.
    /// </summary>
    public static async Task<RunScopeExpansion> ExpandScheduleAsync(
        CatalogDbContext catalog, Guid repoId, Guid scheduleId, string scheduleName,
        IReadOnlyCollection<string>? batchFilter = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // A distinct, trimmed set of the requested batches; empty means "no filter" (every member runs). Matched
        // against each flow's batch (coalesced to the default), so EF renders it as a SQL IN over the batch column.
        var filter = batchFilter is null
            ? null
            : batchFilter.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim())
                .ToHashSet(StringComparer.Ordinal) is { Count: > 0 } set ? set : null;
        var raw = await (
            from member in catalog.ScheduleMembers.AsNoTracking().Where(m => m.ScheduleId == scheduleId)
            join pipeline in catalog.Pipelines.AsNoTracking() on member.PipelineId equals pipeline.Id
            where pipeline.RepoId == repoId && pipeline.Active
                  && pipeline.ExecutionMode == PipelineExecutionModes.Auto
                  && (filter == null || filter.Contains(pipeline.Batch ?? CatalogPipeline.DefaultBatch))
            orderby pipeline.Wave < 0 ? 0 : pipeline.Wave, pipeline.Name
            select new RunScopeMember(
                pipeline.Name, pipeline.Kind, pipeline.Wave < 0 ? 0 : pipeline.Wave,
                pipeline.Batch ?? CatalogPipeline.DefaultBatch, pipeline.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        return new RunScopeExpansion(RunScope.Flow, scheduleName, DenseWaves(raw));
    }

    /// <summary>
    /// Renumbers a set's waves to a dense 0..N sequence over exactly the members it contains, preserving their
    /// order. A fire's waves are then relative to what it actually runs: a batch-filtered subset starts at wave 0
    /// rather than at whatever global wave its first member happened to sit on. The group claim gate only cares
    /// about the relative order, so the preview and the run group's step column show the same wave numbers as
    /// what executes.
    /// </summary>
    private static IReadOnlyList<RunScopeMember> DenseWaves(IReadOnlyList<RunScopeMember> members)
    {
        if (members.Count == 0)
        {
            return members;
        }

        var rank = members.Select(m => m.Wave).Distinct().OrderBy(w => w)
            .Select((wave, index) => (wave, index))
            .ToDictionary(t => t.wave, t => t.index);
        return members.Select(m => m with { Wave = rank[m.Wave] }).ToList();
    }

    private static string RequireAnchor(string? anchorFlow)
    {
        var trimmed = anchorFlow?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("A flow-scoped run requires an anchor flow name.", nameof(anchorFlow));
        }

        return trimmed;
    }
}
