namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// How one type a version of a partition's cache holds compares with the version before it, by the type's content hash
/// (<see cref="ReferenceType.ContentHash"/>). A version is written when anything in the partition's cache moved, which may
/// be one type among many, or only the partition's system properties; this is what says which of its types moved with it.
/// </summary>
public enum CacheTypeChange
{
    /// <summary>The version before did not hold the type (or there was none): the type arrived with this version.</summary>
    Added,

    /// <summary>The version before held the type with other content: a record arrived, left, or holds other values.</summary>
    Changed,

    /// <summary>The version before held exactly the same records of the type with exactly the same values.</summary>
    Unchanged,
}

/// <summary>What a version did to each type, against the version it replaced.</summary>
/// <param name="Types">Every type the version holds, by name (ignoring case), and how it compares with the version before.</param>
/// <param name="Removed">The types the version before held that this one does not, by the name that version held them under.</param>
public sealed record CacheTypeChanges(IReadOnlyDictionary<string, CacheTypeChange> Types, IReadOnlyList<string> Removed)
{
    /// <summary>How the type <paramref name="name"/> compares with the version before, or null when the version holds no such type.</summary>
    public CacheTypeChange? Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Types.TryGetValue(name, out var change) ? change : null;
    }

    /// <summary>The types that arrived or changed, in ordinal order of their names.</summary>
    public IReadOnlyList<string> Moved
        => Types.Where(t => t.Value != CacheTypeChange.Unchanged).Select(t => t.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Compares every type of <paramref name="current"/> with the same type in <paramref name="previous"/> by its content hash.
    /// A type is unchanged only when the version before held it under exactly the same name with the same hash; a name
    /// spelled another way is kept apart, since the records are stored under the name as spelled.
    /// </summary>
    public static CacheTypeChanges Compare(ReferenceSnapshot? previous, ReferenceSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var types = new Dictionary<string, CacheTypeChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in current.Types)
        {
            var before = previous?.Type(type.Name);
            types[type.Name] = before is null
                ? CacheTypeChange.Added
                : string.Equals(before.Name, type.Name, StringComparison.Ordinal) && string.Equals(before.ContentHash(), type.ContentHash(), StringComparison.Ordinal)
                    ? CacheTypeChange.Unchanged
                    : CacheTypeChange.Changed;
        }

        var removed = (previous?.Types ?? [])
            .Where(type => !current.HasType(type.Name))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new CacheTypeChanges(types, removed);
    }

    /// <summary>Every type of <paramref name="snapshot"/> unchanged: what a merge that wrote no version did to the cache.</summary>
    public static CacheTypeChanges None(ReferenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CacheTypeChanges(
            snapshot.Types.ToDictionary(t => t.Name, _ => CacheTypeChange.Unchanged, StringComparer.OrdinalIgnoreCase), []);
    }

    /// <summary>The change as a version row, the API and the CLI write it: added, changed or unchanged.</summary>
    public static string Text(CacheTypeChange change) => change switch
    {
        CacheTypeChange.Added => "added",
        CacheTypeChange.Changed => "changed",
        _ => "unchanged",
    };

    /// <summary>The change <paramref name="text"/> names exactly as <see cref="Text"/> writes it, or null for anything else.</summary>
    public static CacheTypeChange? Parse(string? text) => text switch
    {
        "added" => CacheTypeChange.Added,
        "changed" => CacheTypeChange.Changed,
        "unchanged" => CacheTypeChange.Unchanged,
        _ => null,
    };
}
