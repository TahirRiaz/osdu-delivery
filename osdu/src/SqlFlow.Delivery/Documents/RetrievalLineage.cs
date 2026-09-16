using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a retrieval flow contributes to SQLFlow's lineage (docs/lineage-design.md section 3): it reads every OSDU kind it
/// retrieves (wildcards allowed) on its platform and partition, and lands the records as JSON Lines files, with a manifest
/// per run, under its target location. The landing is a file drop, so a file flow reading that folder is ordered after the
/// retrieval, exactly as it is after any flow landing files.
/// </summary>
public static class RetrievalLineage
{
    /// <summary>The file name glob of the record files a run writes (<c>part-00001.jsonl</c>).</summary>
    public const string RecordFiles = "part-*.jsonl";

    /// <summary>Everything the flow contributes: the kinds in declaration order, then the record files and the manifest.</summary>
    public static RegisteredFlowLineage Describe(RetrievalDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var who = $"retrieval flow '{flow.Name}'";
        var warnings = new List<string>();
        var datasets = new List<DeclaredDataset>();
        var partition = OsduLineage.Partition(flow.Source.Headers, who, "source.headers", warnings);
        if (partition is not null)
        {
            foreach (var kind in flow.Source.Kinds)
            {
                if (OsduLineage.Type(LineageRelation.Reads, flow.Source.Endpoint, partition, kind, who, $"source.kinds '{kind}'", warnings) is { } read)
                {
                    datasets.Add(read);
                }
            }
        }

        var location = flow.Target.Location.Trim();
        if (IsRelativeLocalPath(location))
        {
            // The runner resolves a relative location against the working directory of the process that runs it, while
            // every other location of a flow is relative to the flow file. Lineage places it as the flow file would, and
            // says so, because the files may land somewhere else.
            warnings.Add(
                $"{who}: target.location '{location}' is a relative path; a retrieval run resolves it against the working directory of the process running it, not the flow file, so the lineage node (relative to the flow file) may not be where the files land. Use an absolute path or a storage URI.");
        }

        var files = new List<DeclaredFileLocation>
        {
            new() { Relation = LineageRelation.Writes, Location = location, FilePattern = RecordFiles + (flow.Target.Gzip ? ".gz" : string.Empty) },
            new() { Relation = LineageRelation.Writes, Location = location, FilePattern = flow.Target.Manifest },
        };

        return new RegisteredFlowLineage { Files = files, Datasets = datasets, Warnings = warnings };
    }

    private static bool IsRelativeLocalPath(string location)
        => !location.Contains("://", StringComparison.Ordinal)
            && !location.StartsWith("${", StringComparison.Ordinal)
            && !location.StartsWith('@')
            && !Path.IsPathRooted(location);
}
