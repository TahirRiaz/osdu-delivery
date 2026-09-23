using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a cache flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3): it reads each declared OSDU type's
/// kind (wildcards allowed) on its platform and partition, and each dictionary type's file in the repository, and writes
/// each declared type into its partition's cache. A delivery flow reading a cache type is so ordered after the cache flow
/// writing it, and a cache flow reading a kind a delivery flow writes after that delivery flow.
/// </summary>
public static class CacheLineage
{
    /// <summary>
    /// Everything the flow contributes, in declaration order. A dictionary's file is found as a refresh finds it, inside
    /// the estate <paramref name="context"/> scans; without a context no file is read and none is declared.
    /// </summary>
    public static RegisteredFlowLineage Describe(CacheDefinition flow, RegisteredLineageContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"cache flow '{flow.Name}'";
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

        return new RegisteredFlowLineage { Datasets = datasets, Files = files, Warnings = warnings };
    }

    /// <summary>The dictionary's file relative to the flow's folder, as a file location is declared, or null when there is none.</summary>
    private static string? DictionaryFile(string name, RegisteredLineageContext context)
    {
        if (DictionaryCatalog.Locate(context.DocumentFolder, context.Contains) is not { } directory)
        {
            return null;
        }

        var file = new[] { name + ".yaml", name + ".yml" }.Select(f => Path.Combine(directory, f)).FirstOrDefault(File.Exists);
        return file is null ? null : Path.GetRelativePath(context.DocumentFolder, file).Replace('\\', '/');
    }
}
