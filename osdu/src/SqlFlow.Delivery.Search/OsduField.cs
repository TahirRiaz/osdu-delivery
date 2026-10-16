namespace SqlFlow.Delivery.Search;

/// <summary>How the indexer stores a property whose value a query compares, which decides how a query asks for it.</summary>
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
    /// A keyword with no sub-field: a legacy <c>^srn</c> link (<c>TypeMapper.getKeywordIndexerMapping</c>), a value
    /// inside a property the schema marks <c>x-osdu-indexing: flattened</c>, which Elasticsearch stores as keywords, or one
    /// of the record's own keyword properties (<c>kind</c>, <c>acl.viewers</c>, <c>legal.legaltags</c>, a tag). An exact
    /// match asks the property itself.
    /// </summary>
    Keyword,

    /// <summary>A number (<c>integer</c>, <c>long</c>, <c>float</c> or <c>double</c>), compared as the number it is.</summary>
    Number,

    /// <summary>A boolean, compared as <c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>A date, compared as the instant its text names.</summary>
    Date,
}

/// <summary>
/// A property a query compares, as the platform indexes it: its path from the record root, how its value is stored,
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

    /// <summary>
    /// The field an exact comparison, a range and an aggregation ask, relative to the nested array when there is one: the
    /// <c>keyword</c> sub-field of text, which holds the whole value unanalysed, and the property itself otherwise.
    /// </summary>
    public string ExactPath => Index == OsduFieldIndex.Text ? $"{QueryPath}.{OsduQuery.KeywordSubField}" : QueryPath;

    /// <summary>
    /// The value the search's <c>aggregateBy</c> names the property by: the field an exact comparison asks, inside the
    /// service's <c>nested(path, field)</c> form for a property of a nested array, which the service parses into a nested
    /// aggregation over the array's objects (<c>AggregationParserUtil.parseAggregation</c>). A text property aggregated by
    /// its analysed field returns nothing, since Elasticsearch aggregates only unanalysed values.
    /// </summary>
    public string AggregateBy => NestedPath is null ? ExactPath : $"nested({NestedPath}, {ExactPath})";

    /// <summary>A string property: analysed text, with the whole value in its keyword sub-field.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Text(string path, string? nestedPath = null) => Of(path, OsduFieldIndex.Text, nestedPath);

    /// <summary>A keyword property with no sub-field: a <c>^srn</c> link, a value inside a flattened array, or a keyword of the record itself.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Keyword(string path, string? nestedPath = null) => Of(path, OsduFieldIndex.Keyword, nestedPath);

    /// <summary>A number property.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Number(string path, string? nestedPath = null) => Of(path, OsduFieldIndex.Number, nestedPath);

    /// <summary>A boolean property.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Boolean(string path, string? nestedPath = null) => Of(path, OsduFieldIndex.Boolean, nestedPath);

    /// <summary>A date property.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Date(string path, string? nestedPath = null) => Of(path, OsduFieldIndex.Date, nestedPath);

    /// <summary>A property of <paramref name="index"/>'s shape.</summary>
    /// <param name="path">The property's path from the record root.</param>
    /// <param name="index">How the value is stored.</param>
    /// <param name="nestedPath">The nested array it sits in, or null.</param>
    /// <exception cref="OsduQueryException">A path cannot be written in a query, or the property is not inside the array.</exception>
    public static OsduField Of(string path, OsduFieldIndex index, string? nestedPath = null)
    {
        if (!Enum.IsDefined(index))
        {
            throw new OsduQueryException($"'{index}' is not a way the indexer stores a property.");
        }

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

    public override string ToString() => NestedPath is null ? $"{Path} ({Describe(Index)})" : $"{Path} ({Describe(Index)}, nested in {NestedPath})";

    /// <summary>How a stored shape reads in a message.</summary>
    public static string Describe(OsduFieldIndex index) => index switch
    {
        OsduFieldIndex.Text => "text with a keyword sub-field",
        OsduFieldIndex.Keyword => "keyword",
        OsduFieldIndex.Number => "number",
        OsduFieldIndex.Boolean => "boolean",
        _ => "date",
    };
}
