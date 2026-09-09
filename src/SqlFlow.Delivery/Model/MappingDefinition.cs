namespace SqlFlow.Delivery.Model;

/// <summary>
/// The render-affecting half of the document model (design.md sections 4.2, 4.3 and 9). A mapping is a template
/// interpreted at render time. It declares only what the OSDU schema cannot know: source bindings, transforms, the
/// natural key and envelope policy. Types, requiredness and relationship targets come from the pinned schema.
/// </summary>
public sealed record MappingDefinition
{
    public const string DocumentTypeName = "mapping";

    public string? SourcePath { get; init; }

    public required string Name { get; init; }

    /// <summary>Semantic version of the mapping. Part of the render context and therefore of every content hash.</summary>
    public required string Version { get; init; }

    /// <summary>The OSDU kind, which pins the target schema (design.md section 4.1).</summary>
    public required string Kind { get; init; }

    public string? Description { get; init; }

    public required MappingSource Source { get; init; }

    public required MappingIdentity Identity { get; init; }

    public required MappingEnvelope Envelope { get; init; }

    /// <summary>Parameters the mapping accepts from the flow. Values enter the hash (design.md section 9.5).</summary>
    public IReadOnlyDictionary<string, MappingParameter> Parameters { get; init; } = new Dictionary<string, MappingParameter>(StringComparer.Ordinal);

    public required IReadOnlyList<MappingProperty> Properties { get; init; }

    /// <summary>Reusable nested-object definitions referenced by <see cref="MappingProperty.Definition"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MappingProperty>> Definitions { get; init; } = new Dictionary<string, IReadOnlyList<MappingProperty>>(StringComparer.Ordinal);

    /// <summary>Whole-document fixtures: a source record and the exact document it must render to.</summary>
    public IReadOnlyList<MappingFixture> Fixtures { get; init; } = [];

    public string Reference => Name + "@" + Version;

    public string EntityType => SqlFlow.Delivery.Identity.TargetId.EntityTypeFromKind(Kind);
}

public sealed record MappingParameter
{
    public bool Required { get; init; }

    public string? Default { get; init; }

    public string? Description { get; init; }
}

/// <summary>What the mapping expects from the drop: the source system name and the row scopes it binds to.</summary>
public sealed record MappingSource
{
    /// <summary>The source system, entering the delivery key (for example recall).</summary>
    public required string System { get; init; }

    /// <summary>Child scopes the mapping binds collections to; each yields zero or more rows per record.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];
}

/// <summary>
/// Identity lives in the mapping, not the flow (design.md section 9.4). The natural key names mapped properties; the
/// source columns they bind to form the source key, from which the delivery key is derived.
/// </summary>
public sealed record MappingIdentity
{
    /// <summary>Target property paths whose source bindings form the natural key, in order.</summary>
    public required IReadOnlyList<string> NaturalKey { get; init; }

    /// <summary>
    /// Optional human-readable label template over root-scope columns, e.g. "{wellbore_uwi} {log_name} run {log_run}".
    /// Stored on the ledger record for search and display; never part of the document or its hash.
    /// </summary>
    public string? Label { get; init; }
}

public sealed record MappingEnvelope
{
    public required IReadOnlyList<string> LegalTags { get; init; }

    public required IReadOnlyList<string> OtherRelevantDataCountries { get; init; }

    public required MappingAcl Acl { get; init; }

    /// <summary>Static tags stamped on every record.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record MappingAcl
{
    public required IReadOnlyList<string> Owners { get; init; }

    public required IReadOnlyList<string> Viewers { get; init; }
}

/// <summary>The closed transform vocabulary (design.md section 4.2). Anything else is a parse error.</summary>
public enum MappingTransform
{
    /// <summary>Copy the source value, coerced to the schema type.</summary>
    None,

    /// <summary>Emit config.value.</summary>
    Constant,

    Trim,

    Upper,

    Lower,

    /// <summary>Split on config.delimiter and take config.index.</summary>
    Split,

    /// <summary>True when the source equals config.resolve (case-insensitive).</summary>
    Equals,

    /// <summary>Look the source value up in config.values; config.default otherwise.</summary>
    Map,

    /// <summary>Resolve a reference-data or master-data record id from the reference snapshot.</summary>
    Reference,

    /// <summary>Read a value out of the cached record the source value matches (design.md section 6.2).</summary>
    Lookup,

    /// <summary>Compute the id of a record this system also delivers (design.md section 5.3).</summary>
    DeliveredReference,

    /// <summary>Format a template with {column} tokens from the current scope.</summary>
    Template,

    /// <summary>Parse the source as a date/time and emit RFC 3339 UTC.</summary>
    DateTime,
}

/// <summary>What to do when a reference lookup finds nothing.</summary>
public enum ReferenceMiss
{
    /// <summary>Hold the record (terminal until intervention).</summary>
    Hold,

    /// <summary>Omit the property.</summary>
    Omit,

    /// <summary>Fail the render.</summary>
    Error,
}

public sealed record MappingProperty
{
    /// <summary>Dotted JSON path from the record root, e.g. data.Name, or CurveID inside a definition.</summary>
    public required string Target { get; init; }

    /// <summary>Source column in the current scope. Null for constants, templates and objects.</summary>
    public string? Source { get; init; }

    public string? Description { get; init; }

    public MappingTransform Transform { get; init; } = MappingTransform.None;

    public TransformConfig Config { get; init; } = new();

    /// <summary>When true, the property is an array of objects rendered once per row of <see cref="Scope"/>.</summary>
    public bool Collection { get; init; }

    /// <summary>The child scope a collection iterates over.</summary>
    public string? Scope { get; init; }

    /// <summary>Inline nested properties (object or collection item).</summary>
    public IReadOnlyList<MappingProperty> Properties { get; init; } = [];

    /// <summary>Name of a reusable definition supplying the nested properties.</summary>
    public string? Definition { get; init; }

    /// <summary>Per-property fixtures: a source value and the expected rendered value.</summary>
    public IReadOnlyList<PropertyExample> Examples { get; init; } = [];

    public bool IsObject => Properties.Count > 0 || Definition is not null;
}

public sealed record TransformConfig
{
    public string? Value { get; init; }

    public string? Delimiter { get; init; }

    public int? Index { get; init; }

    public string? Resolve { get; init; }

    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Default { get; init; }

    /// <summary>Reference type in the snapshot (UnitOfMeasure) or the entity type (master-data--Wellbore).</summary>
    public string? Type { get; init; }

    /// <summary>Fields of the reference item compared against the (mapped) source value, in order.</summary>
    public IReadOnlyList<string> MatchBy { get; init; } = [];

    /// <summary>For Lookup: the cached path to read out of the matched item (default the record id).</summary>
    public string? Select { get; init; }

    /// <summary>Normalisation applied before matching a reference.</summary>
    public IReadOnlyDictionary<string, string> ValueMap { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ReferenceMiss OnMiss { get; init; } = ReferenceMiss.Hold;

    /// <summary>For DeliveredReference: the source system and key columns of the referenced record.</summary>
    public string? System { get; init; }

    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>For Template: the format with {column} tokens.</summary>
    public string? Format { get; init; }

    /// <summary>For DateTime: an explicit input format; null tries ISO 8601 and common forms.</summary>
    public string? InputFormat { get; init; }
}

public sealed record PropertyExample
{
    /// <summary>The source value (scalar) or, for templates, the row as name/value pairs.</summary>
    public string? Source { get; init; }

    public IReadOnlyDictionary<string, string?> Row { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>The expected rendered value as JSON text (a bare string is quoted automatically).</summary>
    public required string Target { get; init; }
}

public sealed record MappingFixture
{
    public required string Name { get; init; }

    /// <summary>Root-scope row.</summary>
    public required IReadOnlyDictionary<string, string?> Record { get; init; }

    /// <summary>Child-scope rows by scope name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> Scopes { get; init; }
        = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.Ordinal);

    /// <summary>Parameter values for the fixture render.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The expected document (JSON text, compared canonically).</summary>
    public required string Expected { get; init; }
}
