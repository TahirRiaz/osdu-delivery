using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// Where a submission may point at payload files (design.md section 3.4). A submission carries locations rather than
/// bytes: nothing is uploaded, nothing is staged, and the node opens each location with its own identity when the run
/// delivers. That identity can read whatever it has been granted, so an unguarded location would let a caller have any
/// readable file shipped to OSDU. The roots a flow allows are the ones it declares under
/// <c>source.manualSubmissionFileRoots</c>, or, when it declares none, the place its own drops live.
/// </summary>
public static class PayloadRoots
{
    /// <summary>The prefixes a submission to <paramref name="flow"/> may point inside, in the order they were declared.</summary>
    public static IReadOnlyList<string> Of(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Source.ManualSubmissionFileRoots.Count > 0)
        {
            return flow.Source.ManualSubmissionFileRoots.Select(Normalize).ToList();
        }

        return DropRoot(flow.Source.Location) is { } root ? [root] : [];
    }

    /// <summary>
    /// Why <paramref name="location"/> is not somewhere this flow may be pointed at, or null when it is. The location
    /// has to sit under one of the flow's roots and may not climb out of it.
    /// </summary>
    public static string? Refusal(FlowDefinition flow, string location)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (string.IsNullOrWhiteSpace(location))
        {
            return "a payload location is required; it is the folder or glob the record's files already sit in.";
        }

        if (location.Contains("..", StringComparison.Ordinal))
        {
            return $"payload location '{location}' must not contain '..'.";
        }

        var roots = Of(flow);
        if (roots.Count == 0)
        {
            return $"flow '{flow.Name}' declares no source.manualSubmissionFileRoots and no usable drop location, so there is nowhere a submission may point at files.";
        }

        var candidate = Normalize(location);
        return roots.Any(root => IsUnder(candidate, root))
            ? null
            : $"payload location '{location}' is outside what flow '{flow.Name}' allows. A submission may point inside: {string.Join(", ", roots)}.";
    }

    /// <summary>True when <paramref name="location"/> is the root itself or sits under it.</summary>
    public static bool IsUnder(string location, string root)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(root);
        var candidate = Normalize(location);
        var prefix = Normalize(root);
        return candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The fixed part of a flow's declared drop location: everything before its first <c>{parameter}</c> token, cut back
    /// to a folder boundary. A flow reading <c>abfss://lake@acct/recall/{logSource}</c> allows <c>abfss://lake@acct/recall</c>,
    /// so every drop of that flow, whatever its parameters, is inside.
    /// </summary>
    public static string? DropRoot(string declaredLocation)
    {
        if (string.IsNullOrWhiteSpace(declaredLocation))
        {
            return null;
        }

        var normalized = Normalize(declaredLocation);
        var token = normalized.IndexOf('{', StringComparison.Ordinal);
        if (token >= 0)
        {
            var slash = normalized.LastIndexOf('/', Math.Max(token - 1, 0));
            normalized = slash > 0 ? normalized[..slash] : string.Empty;
        }

        normalized = normalized.TrimEnd('/');
        // A bare scheme or drive letter is not a root anyone should be allowed to point inside of.
        return normalized.Length == 0 || normalized.EndsWith(':') || normalized.EndsWith("//", StringComparison.Ordinal)
            ? null
            : normalized;
    }

    private static string Normalize(string location) => location.Trim().Replace('\\', '/').TrimEnd('/');
}
