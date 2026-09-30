using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a dimension flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3): it reads the kind (wildcards
/// allowed) of each of its dimensions on its platform and partition, and the dictionary file each map step names, and writes
/// each dimension in its partition. A dimension flow is so ordered after the delivery flows writing the kinds it reads, and
/// a cache flow holding one of its dimensions after it.
/// </summary>
public static class DimensionLineage
{
    /// <summary>
    /// Everything the flow contributes, in declaration order. A flow that names its partitions contributes for every one of
    /// them; one that leaves them to the registry is described once, under the partition that stands for every registered one.
    /// A dictionary's file is found as a build finds it, inside the estate <paramref name="context"/> scans.
    /// </summary>
    public static RegisteredFlowLineage Describe(DimensionFlowDefinition flow, RegisteredLineageContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"dimension flow '{flow.Name}'";
        var warnings = new List<string>();
        var partitions = flow.DeclaresPartitions && flow.Partition is null
            ? flow.Partitions
            : flow.FollowsRegistry && flow.Partition is null
                ? [PartitionNames.Every]
                : OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings) is { } one ? [one] : [];

        var datasets = new List<DeclaredDataset>();
        foreach (var partition in partitions)
        {
            foreach (var dimension in flow.Dimensions.Where(d => d.BuildsIn(partition == PartitionNames.Every ? null : partition)))
            {
                var what = $"dimension '{dimension.Name}'";
                if (OsduLineage.Type(LineageRelation.Reads, flow.Source.Endpoint, partition, dimension.Kind, who, what, warnings) is { } read
                    && !datasets.Contains(read))
                {
                    datasets.Add(read);
                }

                if (OsduLineage.Dimension(LineageRelation.Writes, partition, flow.Name, dimension.Name, who, warnings) is { } write && !datasets.Contains(write))
                {
                    datasets.Add(write);
                }
            }
        }

        var files = new List<DeclaredFileLocation>();
        if (context is not null)
        {
            foreach (var name in flow.Dimensions.SelectMany(d => d.Clean).Where(s => s.Kind == CleanStepKind.Map).Select(s => s.Dictionary!).Distinct(StringComparer.Ordinal))
            {
                if (CacheLineage.DictionaryFile(name, context) is { } location)
                {
                    files.Add(new DeclaredFileLocation { Relation = LineageRelation.Reads, Location = location });
                }
                else
                {
                    warnings.Add($"{who}: a map step names dictionary {name}, which was not found in a {DictionaryCatalog.DirectoryName}/ directory above the flow, so its lineage leaves the file out.");
                }
            }
        }

        return new RegisteredFlowLineage { Datasets = datasets, Files = files, Warnings = warnings };
    }
}
