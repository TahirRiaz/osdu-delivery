using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Documents;

/// <summary>The shape of an OSDU kind as a flow document names one: <c>authority:source:entityType:version</c>, with wildcards per segment.</summary>
internal static partial class OsduKind
{
    /// <summary>The group of a kind whose entity type is wildcarded in its group part.</summary>
    public const string AnyGroup = "*";

    /// <summary>True when <paramref name="kind"/> has the four segments of a kind, each allowing wildcards.</summary>
    public static bool IsValid(string kind) => Pattern().IsMatch(kind);

    /// <summary>True when <paramref name="kind"/> carries no wildcard.</summary>
    public static bool IsExact(string kind) => !kind.Contains('*', StringComparison.Ordinal);

    /// <summary>The entity type inside a kind (osdu:wks:reference-data--UnitOfMeasure:1.0.0), or null when the segment is missing or a wildcard.</summary>
    public static string? EntityType(string kind)
    {
        var segments = kind.Split(':');
        return segments.Length >= 3 && !segments[2].Contains('*', StringComparison.Ordinal) ? segments[2] : null;
    }

    /// <summary>
    /// The group a kind's entity type belongs to: the part before <c>--</c> (<c>master-data</c>, <c>reference-data</c>,
    /// <c>work-product-component</c>, <c>dataset</c>), the whole entity type when it has no group part (<c>Manifest</c>),
    /// or <see cref="AnyGroup"/> when that part carries a wildcard or the kind has no entity type segment.
    /// </summary>
    public static string Group(string kind)
    {
        var segments = kind.Split(':');
        if (segments.Length < 3)
        {
            return AnyGroup;
        }

        var entityType = segments[2];
        var separator = entityType.IndexOf("--", StringComparison.Ordinal);
        var group = separator > 0 ? entityType[..separator] : entityType;
        return group.Length == 0 || group.Contains('*', StringComparison.Ordinal) ? AnyGroup : group;
    }

    [GeneratedRegex(@"^[\w.*-]+:[\w.*-]+:[\w.*-]+:[\d.*]+$")]
    private static partial Regex Pattern();
}
