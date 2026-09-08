using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Where a flow's mappings and snapshots live. A flow repository keeps them next to its flows (design.md section
/// 10.1): <c>mappings/</c> holds the pinned mapping documents, <c>snapshots/</c> the schema and reference
/// snapshots. A flow may name either explicitly under <c>render</c> (relative to the flow file, or a storage URI
/// for snapshots); otherwise the nearest directory of that name walking up from the flow file is used, so a flow
/// three folders deep in a repository still finds the repository's shared mappings.
/// </summary>
public sealed record DeliveryLayout(string MappingsDirectory, string SnapshotsRoot)
{
    public const string MappingsDirectoryName = "mappings";

    public const string SnapshotsDirectoryName = "snapshots";

    private const int MaxAscent = 16;

    public static DeliveryLayout Resolve(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var baseDirectory = flow.SourcePath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        return new DeliveryLayout(
            Locate(baseDirectory, flow.Render.MappingsDirectory, MappingsDirectoryName),
            Locate(baseDirectory, flow.Render.SnapshotsDirectory, SnapshotsDirectoryName));
    }

    private static string Locate(string baseDirectory, string? declared, string name)
    {
        if (!string.IsNullOrWhiteSpace(declared))
        {
            return declared.Contains("://", StringComparison.Ordinal)
                ? declared
                : Path.GetFullPath(Path.Combine(baseDirectory, declared));
        }

        var current = baseDirectory;
        for (var i = 0; i < MaxAscent && current is not null; i++)
        {
            var candidate = Path.Combine(current, name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current);
        }

        // Nothing found: report the conventional place next to the flow, so the error names where to create it.
        return Path.Combine(baseDirectory, name);
    }
}
