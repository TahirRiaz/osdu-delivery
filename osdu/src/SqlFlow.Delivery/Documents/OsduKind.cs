using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Documents;

/// <summary>The shape of an OSDU kind as a flow document names one: <c>authority:source:entityType:version</c>, with wildcards per segment.</summary>
internal static partial class OsduKind
{
    /// <summary>True when <paramref name="kind"/> has the four segments of a kind, each allowing wildcards.</summary>
    public static bool IsValid(string kind) => Pattern().IsMatch(kind);

    /// <summary>The entity type inside a kind (osdu:wks:reference-data--UnitOfMeasure:1.0.0), or null when the segment is missing or a wildcard.</summary>
    public static string? EntityType(string kind)
    {
        var segments = kind.Split(':');
        return segments.Length >= 3 && !segments[2].Contains('*', StringComparison.Ordinal) ? segments[2] : null;
    }

    [GeneratedRegex(@"^[\w.*-]+:[\w.*-]+:[\w.*-]+:[\d.*]+$")]
    private static partial Regex Pattern();
}
