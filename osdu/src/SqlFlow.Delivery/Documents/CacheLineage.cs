using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a cache flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3): it reads each declared OSDU type's
/// kind (wildcards allowed) on its platform and partition, each dictionary type's file in the repository, and each dimension
/// type's dimension in its partition, and writes each declared type into its partition's cache. A delivery flow reading a
/// cache type is so ordered after the cache flow writing it, a cache flow reading a kind a delivery flow writes after that
/// delivery flow, and one holding a dimension after the dimension flow building it.
/// </summary>
public static class CacheLineage
{
    /// <summary>
    /// A type a cache flow holds in a partition's cache, as lineage names its node: the partition as lineage keys it, the
    /// name mappings read the type by, and the entity type of the records it holds (<c>lookup--&lt;Name&gt;</c> for a
    /// lookup table).
    /// </summary>
    public sealed record HeldType(string Partition, string Name, string EntityType);

    /// <summary>
    /// The types <paramref name="flow"/> writes into the cache of each partition it serves, exactly the cache type nodes
    /// <see cref="Describe"/> writes: a mapping that checks an id against the cached records of an entity type reads every
    /// one of these holding it (<see cref="DeliveryLineage"/>). A flow whose partition cannot name a node, and a type
    /// whose name cannot, hold nothing here, as they write no node there.
    /// </summary>
    public static IReadOnlyList<HeldType> Held(CacheDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.DeclaresPartitions && flow.Partition is null)
        {
            return flow.Partitions.SelectMany(p => Held(flow.ForPartition(p))).Distinct().ToList();
        }

        var who = Who(flow);
        var unused = new List<string>();
        return PartitionOf(flow, who, unused) is { } partition
            ? flow.Types
                .Where(type => OsduLineage.CacheType(LineageRelation.Writes, partition, type.Name, who, unused) is not null)
                .Select(type => new HeldType(partition, type.Name.Trim(), type.EntityType))
                .Distinct()
                .ToList()
            : [];
    }

    private static string Who(CacheDefinition flow) => $"cache flow '{flow.Name}'";

    /// <summary>
    /// The partition a flow's nodes are keyed by. A flow that leaves its partitions to the registry is described once,
    /// under the partition that stands for every registered one: lineage is computed from the documents, and the registry
    /// is the catalog's.
    /// </summary>
    private static string? PartitionOf(CacheDefinition flow, string who, List<string> warnings)
        => flow.FollowsRegistry && flow.Partition is null
            ? PartitionNames.Every
            : OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings);

    /// <summary>
    /// Everything the flow contributes, in declaration order. A dictionary's file is found as a refresh finds it, inside
    /// the estate <paramref name="context"/> scans; without a context no file is read and none is declared. A flow that names
    /// its partitions contributes for every one of them, bound to it, so each partition's cache types are nodes of their own
    /// (docs/partitions-design.md section 6).
    /// </summary>
    public static RegisteredFlowLineage Describe(CacheDefinition flow, RegisteredLineageContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.DeclaresPartitions && flow.Partition is null)
        {
            var described = flow.Partitions.Select(p => Describe(flow.ForPartition(p), context)).ToList();
            return new RegisteredFlowLineage
            {
                Objects = described.SelectMany(d => d.Objects).Distinct().ToList(),
                Files = described.SelectMany(d => d.Files).Distinct().ToList(),
                Datasets = described.SelectMany(d => d.Datasets).Distinct().ToList(),
                Warnings = described.SelectMany(d => d.Warnings).Distinct(StringComparer.Ordinal).ToList(),
            };
        }
        var who = Who(flow);
        var warnings = new List<string>();
        var datasets = new List<DeclaredDataset>();
        var files = new List<DeclaredFileLocation>();
        if (context is not null)
        {
            foreach (var type in flow.Types.Where(t => t.Origin == CacheOrigin.Dictionary))
            {
                if (DictionaryFile(type.Dictionary!, context) is { } location)
                {
                    files.Add(new DeclaredFileLocation { Relation = LineageRelation.Reads, Location = location });
                }
                else
                {
                    warnings.Add($"{who}: type '{type.Name}' holds dictionary {type.Dictionary}, which was not found in a {DictionaryCatalog.DirectoryName}/ directory above the flow, so its lineage leaves the file out.");
                }
            }
        }

        var partition = PartitionOf(flow, who, warnings);
        if (partition is not null)
        {
            foreach (var type in flow.Types)
            {
                if (type is { Origin: CacheOrigin.Osdu, Kind: { } kind } && flow.Source.Endpoint is { } endpoint
                    && OsduLineage.Type(LineageRelation.Reads, endpoint, partition, kind, who, $"type '{type.Name}'", warnings) is { } read)
                {
                    datasets.Add(read);
                }

                // A dimension type reads what the dimension flow's build wrote, so the cache flow is ordered after it; two types
                // holding one dimension read its node once.
                if (type is { Origin: CacheOrigin.Dimension, Dimension: { } dimension, DimensionFlow: { } dimensionFlow }
                    && OsduLineage.Dimension(LineageRelation.Reads, partition, dimensionFlow, dimension, who, warnings) is { } built
                    && !datasets.Contains(built))
                {
                    datasets.Add(built);
                }

                if (OsduLineage.CacheType(LineageRelation.Writes, partition, type.Name, who, warnings) is { } write)
                {
                    datasets.Add(write);
                }
            }
        }

        return new RegisteredFlowLineage { Objects = DeclaredObjects(flow), Datasets = datasets, Files = files, Warnings = warnings };
    }

    /// <summary>
    /// The ingestion tables the flow's table types read, on the connection its source declares, in declaration order: the
    /// tables of every partition's types, once each.
    /// </summary>
    public static IReadOnlyList<DeclaredDataObject> DeclaredObjects(CacheDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Source.Connection is not { } connection
            ? []
            : flow.Types.Where(t => t.Origin == CacheOrigin.Table).Select(t => DeclaredDataObject.Reads(connection, t.Table!)).Distinct().ToList();
    }

    /// <summary>
    /// The dictionary's file relative to the flow's folder, as a file location is declared, or null when there is none. A
    /// dimension flow's map steps find their dictionaries the same way.
    /// </summary>
    internal static string? DictionaryFile(string name, RegisteredLineageContext context)
    {
        if (DictionaryCatalog.Locate(context.DocumentFolder, context.Contains) is not { } directory)
        {
            return null;
        }

        var file = new[] { name + ".yaml", name + ".yml" }.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists);
        return file is null ? null : Path.GetRelativePath(context.DocumentFolder, file).Replace('\\', '/');
    }
}
