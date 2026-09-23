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

/// <summary>
/// One type a cache holds, as its cache flow declares it: where its records come from (<see cref="Origin"/>) and what of each
/// record the cache keeps. An OSDU type is searched for (<see cref="Kind"/>, <see cref="Query"/>) and keeps paths of each
/// record; a table type reads columns of an ingestion table (<see cref="Table"/>) keyed by one of them; a dictionary type
/// holds the entries of a dictionary document (<see cref="Dictionary"/>), whose key and fields the document names.
/// </summary>
public sealed record ReferenceTypeSpec
{
    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public required string Name { get; init; }

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure), or lookup--&lt;Name&gt; for a type that holds no OSDU records.</summary>
    public required string EntityType { get; init; }

    /// <summary>Where the type's records come from.</summary>
    public CacheOrigin Origin { get; init; } = CacheOrigin.Osdu;

    /// <summary>For an OSDU type: the search kind pattern (osdu:wks:reference-data--UnitOfMeasure:*).</summary>
    public string? Kind { get; init; }

    /// <summary>For a table type: the three-part name of the ingestion table read.</summary>
    public string? Table { get; init; }

    /// <summary>
    /// For a table type: the column each row is keyed by; for a dictionary type, the name its document gives the key, known
    /// once the document is read. The key is also kept as a field under this name, so a mapping matches on it by name.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>For a dictionary type: the name of the dictionary document whose entries the type holds.</summary>
    public string? Dictionary { get; init; }

    /// <summary>
    /// For a dictionary type whose document has been found: the document's file, relative to the repository root, as the
    /// catalog records it and messages name it. Null as a cache flow declares the type.
    /// </summary>
    public string? DictionaryPath { get; init; }

    /// <summary>
    /// What the cache keeps of each record: for an OSDU type, paths into it (data.Code, data.NameAlias.AliasName), whatever a
    /// path yields cached as it is; for a table type, columns beside the key. The id is always kept. A dictionary type's
    /// cache flow lists none, since its document names them; they are known once the document is read.
    /// </summary>
    public IReadOnlyList<ReferenceFieldSpec> Fields { get; init; } = [];

    /// <summary>For an OSDU type: the search query narrowing the capture (default *).</summary>
    public string Query { get; init; } = "*";

    /// <summary>
    /// What a change to this type's cached values does to the records built from them: update on the next run (the default)
    /// or, where a flow opts in, wait for an operator to approve the update.
    /// </summary>
    public CacheChangeMode OnChange { get; init; } = CacheChangeMode.Auto;

    /// <summary>True for a type whose records are not OSDU records: a table or a dictionary.</summary>
    public bool IsLookup => Origin != CacheOrigin.Osdu;

    /// <summary>Where the records come from, as a person reads it: the kind searched, the table read, or the dictionary held.</summary>
    public string Describe() => Origin switch
    {
        CacheOrigin.Table => $"table {Table}",
        CacheOrigin.Dictionary => $"dictionary {Dictionary}",
        _ => $"kind {Kind}",
    };

    /// <summary>Rejects a type that captures nothing, two values kept under one name, or a setting of another origin.</summary>
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

        switch (Origin)
        {
            case CacheOrigin.Osdu:
                ValidateOsdu();
                break;
            case CacheOrigin.Table:
                ValidateTable();
                break;
            default:
                ValidateDictionary();
                break;
        }
    }

    private void ValidateOsdu()
    {
        if (string.IsNullOrWhiteSpace(Kind))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs a kind to search.");
        }

        Refuse(Table, "table");
        Refuse(Key, "key");
        Refuse(Dictionary, "dictionary");
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

    private void ValidateTable()
    {
        if (string.IsNullOrWhiteSpace(Table))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs the ingestion table it reads.");
        }

        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs the key: the column each row of {Table} is keyed by.");
        }

        Refuse(Kind, "kind");
        Refuse(Dictionary, "dictionary");
        RefuseQuery();
        ValidateLookupEntityType();
        if (Fields.Count == 0)
        {
            throw new FlowValidationException($"Cached type '{Name}' reads no column of {Table} beside its key; list the columns a mapping reads under fields.");
        }

        ValidateLookupNames(Key.Trim(), $"column of {Table}");
    }

    private void ValidateDictionary()
    {
        if (string.IsNullOrWhiteSpace(Dictionary))
        {
            throw new FlowValidationException($"Cached type '{Name}' needs the name of the dictionary it holds.");
        }

        Refuse(Kind, "kind");
        Refuse(Table, "table");
        RefuseQuery();
        ValidateLookupEntityType();

        // As a cache flow declares it, a dictionary type names only its dictionary; once the document is read, it carries
        // the key and the fields the document names.
        if (Key is null)
        {
            if (Fields.Count > 0)
            {
                throw new FlowValidationException($"Cached type '{Name}' holds dictionary {Dictionary}, whose document names its fields; the cache flow lists none.");
            }

            return;
        }

        ValidateLookupNames(Key.Trim(), $"entry of dictionary {Dictionary}");
    }

    /// <summary>A lookup row's key is its id: neither the key nor a field is called id, and no two share a name.</summary>
    private void ValidateLookupNames(string key, string what)
    {
        if (LookupKeys.IsIdName(key))
        {
            throw new FlowValidationException($"Cached type '{Name}' is keyed by '{key}'; a lookup row's key is its id, so the key of each {what} needs another name.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        foreach (var field in Fields)
        {
            if (LookupKeys.IsIdName(field.Name))
            {
                throw new FlowValidationException($"Cached type '{Name}' keeps '{field.Path}' as '{field.Name}'; a lookup row's key is its id, so keep it under another name.");
            }

            if (string.Equals(field.Path, key, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException($"Cached type '{Name}' lists its key '{key}' among its fields; the key is kept under its own name already.");
            }

            if (!seen.Add(field.Name))
            {
                throw new FlowValidationException($"Cached type '{Name}' keeps two values under the name '{field.Name}'; give one of them a different name.");
            }
        }
    }

    private void ValidateLookupEntityType()
    {
        var expected = ReferenceType.LookupEntityType(Name);
        if (!string.Equals(EntityType, expected, StringComparison.Ordinal))
        {
            throw new FlowValidationException($"Cached type '{Name}' holds no OSDU records, so it is kept as {expected}, not as {EntityType}; leave entityType out.");
        }
    }

    private void RefuseQuery()
    {
        if (!string.IsNullOrWhiteSpace(Query) && Query.Trim() != "*")
        {
            throw new FlowValidationException($"Cached type '{Name}' comes from {Describe()}, which is not searched, so it takes no query: it holds every row.");
        }
    }

    private void Refuse(string? value, string setting)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            throw new FlowValidationException($"Cached type '{Name}' comes from {Describe()}, which takes no '{setting}'. A type has one origin: kind, table or dictionary.");
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
