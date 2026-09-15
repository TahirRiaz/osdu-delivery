using SqlFlow.Core;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// What a change to a cached value does to the records that were built from it. The cache is an input to every
/// document it touches, so a changed value means delivered records no longer match what the cache says; the only
/// question is whether that update goes out on its own or waits for someone to approve it.
/// </summary>
public enum CacheChangeMode
{
    /// <summary>Tag the affected records and wait: nothing reaches OSDU until an operator approves the tag.</summary>
    Approve,

    /// <summary>Tag the affected records and let the next run carry them, no approval step.</summary>
    Auto,
}

/// <summary>What a capture reads into a version of a cache: one entry per reference or master-data type.</summary>
public sealed record ReferenceCaptureSpec
{
    public required IReadOnlyList<ReferenceTypeSpec> Types { get; init; }
}

/// <summary>One type a cache holds, as its cache flow declares it.</summary>
public sealed record ReferenceTypeSpec
{
    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public required string Name { get; init; }

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure).</summary>
    public required string EntityType { get; init; }

    /// <summary>The search kind pattern (osdu:wks:reference-data--UnitOfMeasure:*).</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The paths to capture (data.Code, data.Name, data.NameAlias.AliasName). Whatever a path yields is cached as it is:
    /// a scalar, a set of values, or a nested object. The id is always captured.
    /// </summary>
    public IReadOnlyList<ReferenceFieldSpec> Fields { get; init; } = [];

    /// <summary>The search query narrowing the capture (default *).</summary>
    public string Query { get; init; } = "*";

    /// <summary>
    /// What a change to this type's cached values does to the records built from them: update on the next run (the default)
    /// or, where a flow opts in, wait for an operator to approve the update.
    /// </summary>
    public CacheChangeMode OnChange { get; init; } = CacheChangeMode.Auto;

    /// <summary>Rejects a type that captures nothing, or two paths cached under one name.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new FlowValidationException("A cached type needs a name.");
        }

        if (string.IsNullOrWhiteSpace(EntityType))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs an entityType.");
        }

        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs a kind to search.");
        }

        if (Fields.Count == 0)
        {
            throw new FlowValidationException($"Cached type '{Name}' declares no fields to capture.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            // A field named ID (OSDU reference data has one) is fine and shadows the record id under that name;
            // the exact key 'id' is not, because that is the key the record id itself is written under.
            if (field.Name.Equals("id", StringComparison.Ordinal))
            {
                throw new FlowValidationException($"Cached type '{Name}' caches '{field.Path}' as 'id', which is the key the record id is written under; cache it under another name.");
            }

            if (!seen.Add(field.Name))
            {
                throw new FlowValidationException($"Cached type '{Name}' caches two paths under the name '{field.Name}'; give one of them a different 'as'.");
            }
        }
    }
}

/// <summary>
/// One path to cache, and the name the cache stores it under. Written either as the bare path (<c>data.Code</c>, stored as
/// <c>Code</c>) or as a path with an explicit name (<c>{ path: data.NameAlias.AliasName, as: Alias }</c>). A path crosses
/// arrays implicitly, so a path through an array of objects yields the set of values found along it.
/// </summary>
public sealed record ReferenceFieldSpec
{
    public ReferenceFieldSpec(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path.Trim();
        Name = string.IsNullOrWhiteSpace(name) ? ReferenceField.Normalize(Path) : name.Trim();
    }

    /// <summary>The path into the OSDU record, from its root (<c>data.Code</c>, <c>legal.legaltags</c>).</summary>
    public string Path { get; }

    /// <summary>The name the value is cached under, and the name a mapping matches or selects by.</summary>
    public string Name { get; }
}
