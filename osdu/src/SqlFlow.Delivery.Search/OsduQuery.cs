using System.Text;

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
/// It parses a query holding <c>nested(</c> itself before Elasticsearch sees it (<c>QueryParserUtil</c>), counting
/// backslashes to decide whether a quote is escaped and answering <c>400 Malformed unbalanced double quotes</c> for a
/// query whose quotes do not balance. What this type emits always balances, and a value that parser would misread is
/// refused (<see cref="ServiceParser"/>).
/// </description></item>
/// <item><description>
/// The indexer maps a string property as <c>text</c> with a <c>keyword</c> sub-field, and a <c>keywordLower</c>
/// sub-field where the platform enables it (<c>TypeMapper.getTextIndexerMapping</c>); a legacy <c>^srn</c> link is a
/// bare <c>keyword</c> (<see cref="OsduFieldIndex"/>). The analysed <c>text</c> field matches a phrase anywhere in the
/// value; the keyword matches the whole value exactly. A lookup that must find one record asks the keyword.
/// </description></item>
/// <item><description>
/// The keyword sub-field is mapped with <c>ignore_above: 256</c> and <c>null_value: "null"</c>
/// (<c>TypeMapper.getKeywordMap</c>): a longer value is not indexed there at all, and a property that is null is
/// indexed as the text <c>null</c>. A lookup of either would find nothing or the wrong records, so it is refused rather
/// than sent.
/// </description></item>
/// <item><description>
/// A property the schema marks <c>x-osdu-indexing: nested</c> is queried through the service's own
/// <c>nested(path, (query))</c> form (<c>QueryParserUtil</c>, <c>NestedQueryNode</c>). A plain dotted path into a
/// nested array parses without complaint and matches nothing, and a nested query over an index where the path is not
/// nested matches nothing there either (<c>ignoreUnmapped(true)</c>).
/// </description></item>
/// </list>
/// </remarks>
public sealed record OsduQuery
{
    private OsduQuery(string text, string? termValue)
    {
        Text = text;
        TermValue = termValue;
    }

    /// <summary>The sub-field the indexer gives every text property for exact matching.</summary>
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

    /// <summary>
    /// What the keyword sub-field indexes a null property as (<c>null_value</c>). Looking this text up would find the
    /// records that have no value as well as any record that holds the text.
    /// </summary>
    public const string KeywordNullValue = "null";

    /// <summary>
    /// The most UTF-8 bytes one indexed term can hold (Lucene's <c>IndexWriter.MAX_TERM_LENGTH</c>). A keyword with no
    /// <c>ignore_above</c> cannot hold a longer value, since the indexer could never have stored it.
    /// </summary>
    public const int MaxTermBytes = 32766;

    /// <summary>The query as the service receives it.</summary>
    public string Text { get; }

    /// <summary>The value a single comparison asks for, or null for a group, which a nested query cannot hold.</summary>
    private string? TermValue { get; }

    public override string ToString() => Text;

    /// <summary>
    /// Finds the records whose <paramref name="field"/> is exactly <paramref name="value"/>, whole and unanalysed, asked
    /// the way the platform stores that property: the keyword sub-field of text, the keyword itself otherwise, inside
    /// the service's nested form for a property of a nested array. This is the form a lookup that must identify one
    /// record uses.
    /// </summary>
    /// <param name="field">The property, as the schema of the kind searched says it is indexed.</param>
    /// <param name="value">The whole value the property must equal.</param>
    /// <param name="caseInsensitive">
    /// Ask the <c>keywordLower</c> sub-field of a text property instead. Only where the platform enables it: on a
    /// platform that does not, the sub-field does not exist and the query matches nothing.
    /// </param>
    /// <exception cref="OsduQueryException">The value cannot be asked for exactly, and the message says which rule refuses it.</exception>
    public static OsduQuery Equal(OsduField field, string value, bool caseInsensitive = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        var path = field.Path;
        CheckValue(path, value, "value");

        string term;
        if (field.Index == OsduFieldIndex.Text)
        {
            if (value.Length > KeywordIgnoreAbove)
            {
                throw new OsduQueryException(
                    $"the value compared with '{path}' is {value.Length} characters, and the indexer keeps no more than {KeywordIgnoreAbove} in the '{KeywordSubField}' sub-field (ignore_above), so an exact match on it can never succeed.");
            }

            if (string.Equals(value, KeywordNullValue, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new OsduQueryException(
                    $"the value compared with '{path}' is '{value}', which is what the indexer stores in the '{KeywordSubField}' sub-field for a property that is null (null_value), so the lookup would find every record without a value as well.");
            }

            var sub = caseInsensitive ? KeywordLowerSubField : KeywordSubField;
            term = $"{field.QueryPath}.{sub}:{LuceneText.Phrase(value)}";
        }
        else
        {
            if (caseInsensitive)
            {
                throw new OsduQueryException(
                    $"'{path}' is indexed as a keyword, which has no '{KeywordLowerSubField}' sub-field, so it cannot be compared regardless of case.");
            }

            var bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxTermBytes)
            {
                throw new OsduQueryException(
                    $"the value compared with '{path}' is {bytes} bytes of UTF-8, and an indexed term holds at most {MaxTermBytes}, so no record can hold it.");
            }

            term = $"{field.QueryPath}:{LuceneText.Phrase(value)}";
        }

        if (field.NestedPath is not { } nested)
        {
            return new OsduQuery(term, value);
        }

        return Nested(nested, new OsduQuery(term, value));
    }

    /// <summary>
    /// Finds the records whose text <paramref name="field"/> is exactly <paramref name="value"/>: <see cref="Equal"/>
    /// over a string property outside any nested array, which is how most properties of a schema are indexed.
    /// </summary>
    /// <param name="field">The property path, as a mapping writes it (<c>data.FacilityName</c>).</param>
    /// <param name="value">The whole value the property must equal.</param>
    /// <param name="caseInsensitive">Ask the <c>keywordLower</c> sub-field instead, where the platform has one.</param>
    /// <exception cref="OsduQueryException">The field or the value cannot be asked for exactly.</exception>
    public static OsduQuery Exact(string field, string value, bool caseInsensitive = false)
        => Equal(OsduField.Text(field), value, caseInsensitive);

    /// <summary>
    /// Finds the records whose <paramref name="field"/> contains <paramref name="value"/> as a phrase, by asking the
    /// analysed field. This matches a value that merely begins with or contains the phrase, so it identifies a set
    /// rather than a record; <see cref="Equal"/> is what a lookup uses.
    /// </summary>
    /// <exception cref="OsduQueryException">The field or the value cannot be asked for.</exception>
    public static OsduQuery Phrase(string field, string value)
    {
        var path = OsduPath.Of(field);
        ArgumentNullException.ThrowIfNull(value);
        CheckValue(path, value, "phrase");
        return new OsduQuery($"{path}:{LuceneText.Phrase(value)}", value);
    }

    /// <summary>
    /// Asks <paramref name="inner"/> of the objects inside <paramref name="path"/>, which is how a property the schema
    /// marks <c>x-osdu-indexing: nested</c> is reached. The inner query names its property relative to the path, as the
    /// service's own form does.
    /// </summary>
    /// <remarks>
    /// The inner query is one comparison. The service rewrites the property names inside the nested form by pattern
    /// (<see cref="ServiceParser"/>), and a group of comparisons, or a value that pattern would read as a property, comes
    /// out of that rewriting asking something else.
    /// </remarks>
    /// <exception cref="OsduQueryException">The path cannot name a nested property, or the inner query cannot be carried inside one.</exception>
    public static OsduQuery Nested(string path, OsduQuery inner)
    {
        var parent = OsduPath.Of(path);
        ArgumentNullException.ThrowIfNull(inner);
        if (inner.TermValue is not { } value)
        {
            throw new OsduQueryException(
                $"a nested query over '{parent}' holds one comparison: the search service rewrites the property names inside the nested form by pattern, and a group of comparisons does not survive that rewriting.");
        }

        if (ServiceParser.NestedQueryProblem(value) is { } problem)
        {
            throw new OsduQueryException($"the value compared inside the nested array '{parent}' cannot be asked for: {problem}.");
        }

        return new OsduQuery($"nested({parent}, ({inner.Text}))", termValue: null);
    }

    /// <summary>Every one of <paramref name="queries"/> must hold. One query is itself; none is refused.</summary>
    /// <exception cref="OsduQueryException">No query was given.</exception>
    public static OsduQuery All(params OsduQuery[] queries) => Join("AND", queries);

    /// <summary>Any of <paramref name="queries"/> may hold. One query is itself; none is refused.</summary>
    /// <exception cref="OsduQueryException">No query was given.</exception>
    public static OsduQuery Any(params OsduQuery[] queries) => Join("OR", queries);

    /// <summary>The checks every value passes whatever it is compared with.</summary>
    private static void CheckValue(string path, string value, string what)
    {
        if (value.Length == 0)
        {
            throw new OsduQueryException(
                $"'{path}' cannot be compared with an empty {what}: every record whose property is absent would answer to it, so the match says nothing about which record was meant.");
        }

        if (LuceneText.HasUnquotableCharacter(value))
        {
            throw new OsduQueryException(
                $"the {what} compared with '{path}' holds a control character, which no query can carry, so it can never match what is indexed.");
        }

        if (ServiceParser.AnyQueryProblem(value) is { } problem)
        {
            throw new OsduQueryException($"the {what} compared with '{path}' cannot be asked for: {problem}.");
        }
    }

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
            : new OsduQuery(string.Join($" {op} ", queries.Select(q => $"({q.Text})")), termValue: null);
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
