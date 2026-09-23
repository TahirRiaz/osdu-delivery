namespace SqlFlow.Delivery.Search;

/// <summary>How the indexer stores a property whose value a lookup compares, which decides how a query asks for it.</summary>
public enum OsduFieldIndex
{
    /// <summary>
    /// Analysed text with a <c>keyword</c> sub-field holding the whole value (<c>TypeMapper.getTextIndexerMapping</c>).
    /// This is every string property of a schema, an OSDU id reference included: the indexer types a string as a link,
    /// and so as a bare keyword, only when its pattern starts <c>^srn</c> (<c>PropertiesProcessor.LINK_PREFIX</c>). An
    /// exact match asks the sub-field.
    /// </summary>
    Text,

    /// <summary>
    /// A keyword with no sub-field: a legacy <c>^srn</c> link (<c>TypeMapper.getKeywordIndexerMapping</c>), or a value
    /// inside a property the schema marks <c>x-osdu-indexing: flattened</c>, which Elasticsearch stores as keywords. An
    /// exact match asks the property itself.
    /// </summary>
    Keyword,
}

/// <summary>
/// A property a lookup compares, as the platform indexes it: its path from the record root, how its value is stored,
/// and the nested array it sits in when the schema marks one <c>x-osdu-indexing: nested</c>.
/// </summary>
/// <remarks>
/// The shape comes from the schema of the kind searched, not from the path: <c>data.NameAliases.AliasName</c> and
/// <c>data.FacilitySpecifications.FacilitySpecificationText</c> are written alike and asked for differently, because
/// the first array is nested and the second flattened. A caller that has the schema says which; this type holds it
/// and refuses a shape that cannot be written.
/// </remarks>
public sealed record OsduField
{
    private OsduField(string path, OsduFieldIndex index, string? nestedPath)
    {
        Path = path;
        Index = index;
        NestedPath = nestedPath;
    }

    /// <summary>The property's path from the record root: <c>data.NameAliases.AliasName</c>.</summary>
    public string Path { get; }

    /// <summary>How the value is stored.</summary>
    public OsduFieldIndex Index { get; }

    /// <summary>The nested array the property sits in (<c>data.NameAliases</c>), or null when it sits in none.</summary>
    public string? NestedPath { get; }

    /// <summary>
    /// The path a query names the property by: relative to its nested array inside the service's <c>nested(...)</c>
    /// form, whose parser prefixes it with the array's path (<c>QueryParserUtil.getOneLevelNestedQueryNode</c>), and
    /// the whole path otherwise.
    /// </summary>
    public string QueryPath => NestedPath is null ? Path : Path[(NestedPath.Length + 1)..];

    /// <summary>A string property: analysed text, with the whole value in its keyword sub-field.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Text(string path, string? nestedPath = null) => Of(path, OsduFieldIndex.Text, nestedPath);

    /// <summary>A keyword property with no sub-field: a <c>^srn</c> link, or a value inside a flattened array.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Keyword(string path, string? nestedPath = null) => Of(path, OsduFieldIndex.Keyword, nestedPath);

    public override string ToString() => NestedPath is null ? $"{Path} ({Describe(Index)})" : $"{Path} ({Describe(Index)}, nested in {NestedPath})";

    private static OsduField Of(string path, OsduFieldIndex index, string? nestedPath)
    {
        var checkedPath = OsduPath.Of(path);
        if (nestedPath is null)
        {
            return new OsduField(checkedPath, index, null);
        }

        var parent = OsduPath.Of(nestedPath);
        if (checkedPath.Length <= parent.Length + 1 || !checkedPath.StartsWith(parent + ".", StringComparison.Ordinal))
        {
            throw new OsduQueryException(
                $"'{checkedPath}' is not a property inside the nested array '{parent}': a nested query reaches only the properties of the objects the array holds.");
        }

        return new OsduField(checkedPath, index, parent);
    }

    private static string Describe(OsduFieldIndex index) => index == OsduFieldIndex.Text ? "text with a keyword sub-field" : "keyword";
}
