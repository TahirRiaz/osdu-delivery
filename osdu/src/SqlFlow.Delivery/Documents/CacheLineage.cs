using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a cache flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3): it reads each declared type's
/// OSDU kind (wildcards allowed) on its platform and partition, and writes each declared type into its partition's cache.
/// A delivery flow reading a cache type is so ordered after the cache flow writing it, and a cache flow reading a kind a
/// delivery flow writes after that delivery flow.
/// </summary>
public static class CacheLineage
{
    /// <summary>Everything the flow contributes, in declaration order.</summary>
    public static RegisteredFlowLineage Describe(CacheDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"cache flow '{flow.Name}'";
        var warnings = new List<string>();
        var datasets = new List<DeclaredDataset>();
        var partition = OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings);
        if (partition is not null)
        {
            foreach (var type in flow.Types)
            {
                if (type is { Origin: CacheOrigin.Osdu, Kind: { } kind } && flow.Source.Endpoint is { } endpoint
                    && OsduLineage.Type(LineageRelation.Reads, endpoint, partition, kind, who, $"type '{type.Name}'", warnings) is { } read)
                {
                    datasets.Add(read);
                }

                if (OsduLineage.CacheType(LineageRelation.Writes, partition, type.Name, who, warnings) is { } write)
                {
                    datasets.Add(write);
                }
            }
        }

        return new RegisteredFlowLineage { Datasets = datasets, Warnings = warnings };
    }
}
