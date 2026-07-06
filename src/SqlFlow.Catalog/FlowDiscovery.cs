using SqlFlow.Core.Secrets;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.Catalog;

/// <summary>
/// One <c>*.flow.yaml</c> discovered in a repo during a preview-first scan: enough for a user to select it and
/// preview it before any sync writes to the catalog. <see cref="Content"/> is secret-redacted (and omitted for a
/// pathologically large file). A file that fails to parse still appears (with <see cref="ParseOk"/> false and a
/// <see cref="ParseError"/>) so the selection surface is complete.
/// </summary>
public sealed record DiscoveredFlow(
    string RelativePath, string? FlowName, string? Kind, long SizeBytes, bool ParseOk, string? ParseError, string? Content);

/// <summary>
/// Lists the flow documents under a materialized estate directory WITHOUT importing them, so the GUI can preview a
/// repo's flows and pick a subset before a sync activates them in the catalog. It reuses the exact same
/// <see cref="FlowSetCollector"/> the sync uses, so a flow that parses here parses on sync (one discovery/parse
/// code path), and it enumerates every <c>*.flow.yaml</c> on disk (including unparseable ones) so nothing a user
/// might want to include is hidden. Discovery never touches the database.
/// </summary>
public static class FlowDiscovery
{
    private const string SkippedMarker = ": skipped:";

    /// <summary>Content larger than this is listed and selectable but not previewed (a real flow document is
    /// kilobytes; this only guards a corrupt or hostile file from bloating the preview payload).</summary>
    public const long MaxPreviewContentBytes = 256 * 1024;

    public static IReadOnlyList<DiscoveredFlow> Discover(string estateDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(estateDirectory);
        var root = Path.GetFullPath(estateDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        // The same collector the sync runs: successfully parsed flows plus per-file parse warnings.
        var collected = new FlowSetCollector().Collect(root);
        var parsed = new Dictionary<string, CollectedFlow>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in collected.Flows)
        {
            // A duplicate flow name is possible across files; key by path so each file resolves to itself, and the
            // first occurrence of a given path wins (there is only ever one flow per file).
            parsed.TryAdd(Normalize(flow.Node.File), flow);
        }

        var parseErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var warning in collected.Warnings)
        {
            var marker = warning.IndexOf(SkippedMarker, StringComparison.Ordinal);
            if (marker > 0)
            {
                parseErrors[Normalize(warning[..marker].Trim())] = warning[(marker + SkippedMarker.Length)..].Trim();
            }
        }

        var results = new List<DiscoveredFlow>();
        foreach (var file in Directory.EnumerateFiles(root, "*.flow.yaml", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Normalize(Path.GetRelativePath(root, file));
            try
            {
                var length = new FileInfo(file).Length;
                var content = length <= MaxPreviewContentBytes
                    ? SecretHygiene.RedactedMessage(File.ReadAllText(file))
                    : null;

                if (parsed.TryGetValue(relative, out var flow))
                {
                    results.Add(new DiscoveredFlow(relative, flow.Node.Name, flow.Node.Kind, length, true, null, content));
                }
                else
                {
                    var error = parseErrors.TryGetValue(relative, out var e) ? e : "the document could not be parsed as a flow.";
                    results.Add(new DiscoveredFlow(relative, null, null, length, false, error, content));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new DiscoveredFlow(relative, null, null, 0, false, SecretHygiene.RedactedMessage(ex.Message), null));
            }
        }

        return results.OrderBy(r => r.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
