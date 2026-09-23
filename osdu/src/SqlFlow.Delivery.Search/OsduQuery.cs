namespace SqlFlow.Delivery.Search;

/// <summary>
/// A query string the OSDU search service accepts, built so it asks exactly what the caller meant.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is taken from the services themselves rather than from the API document, which says only "Lucene
/// query string syntax":
/// </para>
/// <list type="bullet">
/// <item><description>
/// The search service builds a <c>query_string</c> query with <c>escape(false)</c> and <c>defaultOperator(OR)</c>
/// (<c>QueryNode.toQueryBuilder</c>). Nothing is escaped for the caller, and a bare multi-word value is read as its
/// words OR'd together, so a value is always a quoted phrase.
/// </description></item>
/// <item><description>
/// It parses the query itself before Elasticsearch sees it (<c>QueryParserUtil</c>), counts backslashes to decide
/// whether a quote is escaped, and answers <c>400 Malformed unbalanced double quotes</c> for a query whose quotes do
/// not balance. What this type emits always balances.
/// </description></item>
/// <item><description>
/// The indexer maps every string property as <c>text</c> with a <c>keyword</c> sub-field, and a <c>keywordLower</c>
/// sub-field where the platform enables it (<c>TypeMapper.getTextIndexerMapping</c>). The analysed <c>text</c> field
/// matches a phrase anywhere in the value; the <c>keyword</c> sub-field matches the whole value exactly. A lookup that
/// must find one record uses the sub-field.
/// </description></item>
/// <item><description>
/// That sub-field is mapped with <c>ignore_above: 256</c> (<c>TypeMapper.getKeywordMap</c>), so a longer value is not
/// indexed there at all and can never match exactly. This type refuses such a lookup rather than emitting a query that
/// silently finds nothing.
/// </description></item>
/// <item><description>
/// A property the schema marks <c>x-osdu-indexing: nested</c> is queried through the service's own
/// <c>nested(path, (query))</c> form (<c>QueryParserUtil</c>, <c>NestedQueryNode</c>). A plain dotted path into a
/// nested array parses without complaint and matches nothing.
/// </description></item>
/// </list>
/// </remarks>
public sealed record OsduQuery
{
    private OsduQuery(string text) => Text = text;

    /// <summary>The sub-field the indexer gives every string property for exact matching.</summary>
    public const string KeywordSubField = "keyword";

    /// <summary>
    /// The sub-field a platform adds when it enables case-insensitive exact matching. It is configuration, not a
    /// guarantee, so nothing here reaches for it unless a caller says the platform has it.
    /// </summary>
    public const string KeywordLowerSubField = "keywordLower";

    /// <summary>
    /// The longest value the keyword sub-field indexes. A longer one is dropped by the indexer, so an exact match on it
    /// cannot succeed whatever the query says.
    /// </summary>
    public const int KeywordIgnoreAbove = 256;

    /// <summary>The query as the service receives it.</summary>
    public string Text { get; }

    public override string ToString() => Text;

    /// <summary>
    /// Finds the records whose <paramref name="field"/> is exactly <paramref name="value"/>, whole and unanalysed, by
    /// asking the keyword sub-field. This is the form a lookup that must identify one record uses.
    /// </summary>
    /// <param name="field">The property path, as a mapping writes it (<c>data.FacilityName</c>).</param>
    /// <param name="value">The whole value the property must equal.</param>
    /// <param name="caseInsensitive">
    /// Ask the <c>keywordLower</c> sub-field instead. Only where the platform enables it: on a platform that does not,
    /// the sub-field does not exist and the query matches nothing.
    /// </param>
    /// <exception cref="OsduQueryException">The field or the value cannot be asked for exactly.</exception>
    public static OsduQuery Exact(string field, string value, bool caseInsensitive = false)
    {
        var path = OsduPath.Of(field);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new OsduQueryException(
                $"'{field}' cannot be matched against an empty value: every record whose property is absent would answer to it, so the match says nothing about which record was meant.");
        }

        if (LuceneText.HasUnquotableCharacter(value))
        {
            throw new OsduQueryException(
                $"the value matched against '{field}' holds a control character, which no query can carry, so it can never match what is indexed.");
        }

        if (value.Length > KeywordIgnoreAbove)
        {
            throw new OsduQueryException(
                $"the value matched against '{field}' is {value.Length} characters, and the indexer stores no more than {KeywordIgnoreAbove} in the '{KeywordSubField}' sub-field (ignore_above), so an exact match on it can never succeed. Match on a shorter property, or ask for a phrase instead of an exact value.");
        }

        var sub = caseInsensitive ? KeywordLowerSubField : KeywordSubField;
        return new OsduQuery($"{path}.{sub}:{LuceneText.Phrase(value)}");
    }

    /// <summary>
    /// Finds the records whose <paramref name="field"/> contains <paramref name="value"/> as a phrase, by asking the
    /// analysed field. This matches a value that merely begins with or contains the phrase, so it identifies a set
    /// rather than a record; <see cref="Exact"/> is what a lookup uses.
    /// </summary>
    /// <exception cref="OsduQueryException">The field or the value cannot be asked for.</exception>
    public static OsduQuery Phrase(string field, string value)
    {
        var path = OsduPath.Of(field);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new OsduQueryException($"'{field}' cannot be matched against an empty phrase.");
        }

        if (LuceneText.HasUnquotableCharacter(value))
        {
            throw new OsduQueryException(
                $"the phrase matched against '{field}' holds a control character, which no query can carry.");
        }

        return new OsduQuery($"{path}:{LuceneText.Phrase(value)}");
    }

    /// <summary>
    /// Asks <paramref name="inner"/> of the objects inside <paramref name="path"/>, which is how a property the schema
    /// marks <c>x-osdu-indexing: nested</c> is reached. The inner query names its properties relative to the path, as
    /// the service's own form does.
    /// </summary>
    /// <exception cref="OsduQueryException">The path cannot name a nested property.</exception>
    public static OsduQuery Nested(string path, OsduQuery inner)
    {
        var parent = OsduPath.Of(path);
        ArgumentNullException.ThrowIfNull(inner);
        return new OsduQuery($"nested({parent}, ({inner.Text}))");
    }

    /// <summary>Every one of <paramref name="queries"/> must hold. One query is itself; none is refused.</summary>
    /// <exception cref="OsduQueryException">No query was given.</exception>
    public static OsduQuery All(params OsduQuery[] queries) => Join("AND", queries);

    /// <summary>Any of <paramref name="queries"/> may hold. One query is itself; none is refused.</summary>
    /// <exception cref="OsduQueryException">No query was given.</exception>
    public static OsduQuery Any(params OsduQuery[] queries) => Join("OR", queries);

    private static OsduQuery Join(string op, OsduQuery[] queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Length == 0)
        {
            throw new OsduQueryException(
                $"an {op} of no queries asks nothing; a query that should match everything is written as '*' by the caller that means it.");
        }

        if (queries.Any(q => q is null))
        {
            throw new OsduQueryException($"an {op} was given a query that is null.");
        }

        // One term needs no grouping, and grouping it would only make the query harder to read in a trace.
        return queries.Length == 1
            ? queries[0]
            : new OsduQuery(string.Join($" {op} ", queries.Select(q => $"({q.Text})")));
    }
}

/// <summary>A query that cannot be built as asked, saying which rule of the platform refuses it.</summary>
public sealed class OsduQueryException : Exception
{
    public OsduQueryException(string message)
        : base(message)
    {
    }

    public OsduQueryException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public OsduQueryException()
    {
    }
}
