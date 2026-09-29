using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What an assertion flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3): it reads the OSDU kind of
/// every test (wildcards allowed) on its platform and partition, and writes nothing. The delivery flow writing a kind is so
/// ordered before the tests of that kind, and a schedule firing both runs the tests once the records have landed.
/// </summary>
public static class AssertionLineage
{
    /// <summary>
    /// Everything the flow contributes, the kinds once each in declaration order. A flow that names its partitions reads in
    /// every one of them; one that leaves them to the registry is described once, under the partition that stands for every
    /// registered one.
    /// </summary>
    public static RegisteredFlowLineage Describe(AssertionFlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"assertion flow '{flow.Name}'";
        var warnings = new List<string>();
        var partitions = flow.DeclaresPartitions && flow.Partition is null
            ? flow.Partitions
            : flow.FollowsRegistry && flow.Partition is null
                ? [PartitionNames.Every]
                : OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings) is { } one ? [one] : [];

        var datasets = new List<DeclaredDataset>();
        foreach (var partition in partitions)
        {
            foreach (var test in flow.Tests.Where(t => t.RunsIn(partition == PartitionNames.Every ? null : partition)))
            {
                if (OsduLineage.Type(LineageRelation.Reads, flow.Source.Endpoint, partition, test.Kind, who, $"test '{test.Name}'", warnings) is { } read
                    && !datasets.Contains(read))
                {
                    datasets.Add(read);
                }
            }
        }

        return new RegisteredFlowLineage { Datasets = datasets, Warnings = warnings };
    }
}
