namespace SqlFlow.Delivery.Identity;

/// <summary>
/// A client-supplied OSDU record id: <c>{partition}:{entityType}:{unique}</c> (design.md section 5.3). The unique
/// segment is the delivery key, so upsert is native and no id bookkeeping is needed.
/// </summary>
public static class TargetId
{
    public static string Compose(string dataPartition, string entityType, DeliveryKey key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPartition);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        return $"{dataPartition}:{entityType}:{key.Value:N}";
    }

    /// <summary>Extracts the entity type ("work-product-component--WellLog") from a kind ("osdu:wks:work-product-component--WellLog:1.4.0").</summary>
    public static string EntityTypeFromKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var parts = kind.Split(':');
        if (parts.Length != 4)
        {
            throw new ArgumentException($"Kind '{kind}' is not in the form authority:source:entityType:version.", nameof(kind));
        }

        return parts[2];
    }

    /// <summary>The schema version segment of a kind ("1.4.0").</summary>
    public static string VersionFromKind(string kind)
    {
        var parts = kind.Split(':');
        return parts.Length == 4 ? parts[3] : throw new ArgumentException($"Kind '{kind}' is malformed.", nameof(kind));
    }

    /// <summary>
    /// Formats a reference to another OSDU record (<c>{partition}:{entityType}:{unique}:</c>, trailing colon for
    /// "latest version"), the form OSDU uses for relationship properties.
    /// </summary>
    public static string Reference(string dataPartition, string entityType, string unique)
        => $"{dataPartition}:{entityType}:{unique}:";

    /// <summary>
    /// A record reference without its version: <c>p:t:k:</c> and <c>p:t:k:123</c> become <c>p:t:k</c>. The unique
    /// segment of an OSDU id may itself hold colons, so only a trailing empty or all-digit segment is taken as the version.
    /// </summary>
    public static string WithoutVersion(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var last = reference.LastIndexOf(':');
        if (last < 0)
        {
            return reference;
        }

        var tail = reference[(last + 1)..];
        var colons = reference.Count(c => c == ':');
        return colons >= 3 && tail.All(char.IsAsciiDigit) ? reference[..last] : reference;
    }

    /// <summary>
    /// Whether <paramref name="value"/> reads as a reference to an OSDU record: a partition, an entity type
    /// (<c>group--Entity</c>) and a unique segment, with or without the version.
    /// </summary>
    public static bool IsRecordReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var parts = value.Split(':');
        return parts.Length >= 3
            && parts[0].Length > 0
            && parts[1].IndexOf("--", StringComparison.Ordinal) is > 0 and var at && at < parts[1].Length - 2
            && parts[2].Length > 0;
    }
}
