using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Extraction;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// The one mapping from extracted script dependencies to lineage facts, shared by every consumer of the
/// extractor (document hooks, run traces, harvested modules) so the relation semantics are identical
/// everywhere. The relations themselves come from <see cref="ScriptDependencies.TypedRelations"/>, the
/// exact DeltaForge lifecycle algorithm; this layer only applies graph hygiene: temp names and identities
/// below the caller's part threshold stay out, and the engine's run-scoped staging (created and THEN
/// dropped by the same script) never reaches the graph: a drop-then-create rebuild, by contrast, keeps its
/// full relations.
/// </summary>
public static class ScriptFactBuilder
{
    public static IEnumerable<LineageFact> Facts(
        ScriptDependencies deps,
        string? flow,
        string? viaModuleKey,
        string serverRef,
        LineageTier tier,
        int minimumParts,
        Guid? runId = null,
        DateTime? observedAtUtc = null,
        string? step = null)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRef);

        foreach (var (table, relation) in deps.TypedRelations())
        {
            if (table.IsTemp || table.PartCount < minimumParts || deps.CreatedThenDropped.Contains(table.Key))
            {
                continue;
            }

            yield return new LineageFact
            {
                Flow = flow,
                ViaModuleKey = viaModuleKey,
                Relation = relation,
                ServerRef = serverRef,
                Database = table.Database,
                Schema = table.Schema,
                Name = table.Name,
                Tier = tier,
                RunId = runId,
                ObservedAtUtc = observedAtUtc,
                Step = step,
            };
        }
    }
}
