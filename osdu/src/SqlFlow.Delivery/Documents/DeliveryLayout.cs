using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Where a flow's mappings live. A flow repository keeps them next to its flows (design.md section 10.1) in
/// <c>mappings/</c>. Templates and caches live in the catalog. A flow may name the directory explicitly under
/// <c>render.mappings</c> (relative to the flow file); otherwise the nearest directory of that name walking up from the
/// flow file is used, so a flow three folders deep in a repository still finds the repository's shared mappings.
/// </summary>
public sealed record DeliveryLayout(string MappingsDirectory)
{
    public const string MappingsDirectoryName = "mappings";

    private const int MaxAscent = 16;

    public static DeliveryLayout Resolve(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var baseDirectory = flow.SourcePath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        return new DeliveryLayout(Locate(baseDirectory, flow.Render.MappingsDirectory, MappingsDirectoryName));
    }

    private static string Locate(string baseDirectory, string? declared, string name)
    {
        if (!string.IsNullOrWhiteSpace(declared))
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, declared));
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
