namespace SqlFlow.Delivery.Order;

/// <summary>
/// OSDU entity types, as an OSDU id and an <c>x-osdu-relationship</c> name them: the group type and the entity type joined
/// by <c>--</c> (<c>master-data--Wellbore</c>). A record's entity type is the third segment of its kind
/// (<c>osdu:wks:master-data--Wellbore:1.3.0</c>), the same whatever the kind's authority, source or version, which is why
/// the order of records is an order of entity types.
/// </summary>
public static class OsduEntity
{
    /// <summary>
    /// OSDU's groups in the order their records are delivered: reference data, master data, datasets, work product
    /// components, work products. Any other group ranks after these.
    /// </summary>
    public static IReadOnlyList<string> Groups { get; } = ["reference-data", "master-data", "dataset", "work-product-component", "work-product"];

    /// <summary>The most characters an entity type may have: OSDU's ids and the ledger's columns stay well inside it.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// The entity type of <paramref name="kind"/> (<c>authority:source:group--Entity:version</c>), or null when it is not a
    /// kind with four segments whose third names a group and an entity.
    /// </summary>
    public static string? OfKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        var segments = kind.Trim().Split(':');
        return segments.Length == 4 && IsEntityType(segments[2]) ? segments[2] : null;
    }

    /// <summary>
    /// The entity type an OSDU id names (<c>dev:master-data--Wellbore:abc</c>, with or without its version), or null when the
    /// id is not shaped <c>partition:group--Entity:code</c>.
    /// </summary>
    public static string? OfId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var first = id.IndexOf(':', StringComparison.Ordinal);
        if (first <= 0)
        {
            return null;
        }

        var second = id.IndexOf(':', first + 1);
        if (second <= first + 1 || second == id.Length - 1)
        {
            return null;
        }

        var entity = id[(first + 1)..second];
        return IsEntityType(entity) ? entity : null;
    }

    /// <summary>Whether <paramref name="text"/> is an entity type: a group and an entity, each of letters, digits, <c>.</c>, <c>_</c> or <c>-</c>.</summary>
    public static bool IsEntityType(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }

        var split = text.IndexOf("--", StringComparison.Ordinal);
        return split > 0 && split + 2 < text.Length && Valid(text.AsSpan(0, split)) && Valid(text.AsSpan(split + 2));
    }

    /// <summary>The group of an entity type (<c>master-data</c>), or the text itself when it names a group alone.</summary>
    public static string GroupOf(string entityTypeOrGroup)
    {
        ArgumentNullException.ThrowIfNull(entityTypeOrGroup);
        var split = entityTypeOrGroup.IndexOf("--", StringComparison.Ordinal);
        return split > 0 ? entityTypeOrGroup[..split] : entityTypeOrGroup;
    }

    /// <summary>The entity of an entity type (<c>Wellbore</c>), or the text itself when it names no group.</summary>
    public static string EntityOf(string entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var split = entityType.IndexOf("--", StringComparison.Ordinal);
        return split > 0 ? entityType[(split + 2)..] : entityType;
    }

    /// <summary>The rank of an entity type's group in <see cref="Groups"/>; a group not listed ranks after all of them.</summary>
    public static int RankOf(string entityTypeOrGroup)
    {
        var group = GroupOf(entityTypeOrGroup);
        for (var i = 0; i < Groups.Count; i++)
        {
            if (string.Equals(Groups[i], group, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return Groups.Count;
    }

    /// <summary>
    /// Whether a relationship target as a schema writes it (<c>master-data--Wellbore</c>, a group alone such as
    /// <c>dataset</c>, or an entity alone such as <c>Wellbore</c>) names <paramref name="entityType"/>. Group and entity
    /// are compared ignoring case, as a source's interface order compares them. A target naming no group matches the group
    /// it names and an entity of that name alike, so a schema that leaves a part out is waited for rather than ignored.
    /// </summary>
    public static bool Names(string target, string entityType)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(entityType);
        if (target.Contains("--", StringComparison.Ordinal))
        {
            return string.Equals(target, entityType, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(target, GroupOf(entityType), StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, EntityOf(entityType), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Valid(ReadOnlySpan<char> part)
    {
        if (part.IsEmpty)
        {
            return false;
        }

        foreach (var c in part)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
