using SqlFlow.Core.Identity;

namespace SqlFlow.Catalog;

/// <summary>
/// The catalog's stable identities. A pipeline's identity is scoped to its repo: the SAME flow name in two
/// different repos is two different pipelines (the catalog spans repos), so the id mixes the repo in. It stays
/// deterministic (no database round-trip), so a run discovered under a repo's estate computes the same pipeline
/// id the registry sync did, and the two join.
/// </summary>
public static class CatalogIdentity
{
    /// <summary>The deterministic, repo-scoped pipeline id for a flow name within a repo.</summary>
    public static Guid Pipeline(Guid repoId, string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return FlowIdentity.FromName($"{repoId:N}/{flowName}");
    }

    /// <summary>The deterministic id of a flow's YAML-declared schedule: one per flow, so re-syncing the same
    /// estate updates the schedule row in place rather than duplicating it. API-created schedules get a fresh id
    /// instead (a flow can carry its git schedule plus ad-hoc API schedules).</summary>
    public static Guid YamlSchedule(Guid repoId, string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return FlowIdentity.FromName($"{repoId:N}/{flowName}/schedule");
    }
}
