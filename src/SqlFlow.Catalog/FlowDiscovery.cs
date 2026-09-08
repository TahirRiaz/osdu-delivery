using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.Catalog;

public sealed record DiscoveredFlow(
    string RelativePath, string? FlowName, string? Kind, long SizeBytes, bool ParseOk, string? ParseError, string? Content);

/// <summary>Previews the flows a folder holds without importing them: the selective-scan wizard's answer to
/// "what would a sync bring in".</summary>
public static class FlowDiscovery
{
    public const long MaxPreviewContentBytes = 256 * 1024;

    public static IReadOnlyList<DiscoveredFlow> Discover(YamlDocumentLoader documents, string estateDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(estateDirectory);
        var root = Path.GetFullPath(estateDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        // The same scanner the sync runs: every *.yaml that parses as a flow. Non-flow yamls are already dropped
        // there, so the preview shows exactly what a sync would import.
        var collected = new EstateScanner(documents).Collect(root);

        var byPath = new Dictionary<string, CollectedFlow>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in collected.Flows)
        {
            byPath.TryAdd(flow.File, flow);
        }

        var results = new List<DiscoveredFlow>();
        foreach (var (relative, flow) in byPath)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(root, relative);
            try
            {
                var length = new FileInfo(full).Length;
                var content = length <= MaxPreviewContentBytes
                    ? SecretHygiene.RedactedMessage(File.ReadAllText(full))
                    : null;
                results.Add(new DiscoveredFlow(relative, flow.Name, flow.Kind, length, true, null, content));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new DiscoveredFlow(
                    relative, flow.Name, flow.Kind, 0, false, SecretHygiene.RedactedMessage(ex), null));
            }
        }

        return results.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ToList();
    }
}
