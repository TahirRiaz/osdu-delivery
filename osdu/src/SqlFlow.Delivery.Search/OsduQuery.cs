using System.Globalization;
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
/// words OR'd together, so a value is always a quoted phrase. Elasticsearch reads a phrase compared with a number, a
/// boolean or a date as a value of that type, so every value is quoted, whatever its type.
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
    private OsduQuery(string text, IReadOnlyList<string>? compared)
    {
        Text = text;
        Compared = compared;
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

    /// <summary>
    /// The values a single comparison carries (one for an equality, the bounds for a range), or null for a group, which a
    /// nested query cannot hold. Each is checked before the comparison can go inside the nested form.
    /// </summary>
    private IReadOnlyList<string>? Compared { get; }

    public override string ToString() => Text;

    /// <summary>
    /// Finds the records whose <paramref name="field"/> is exactly <paramref name="value"/>, whole and unanalysed, asked
    /// the way the platform stores that property: the keyword sub-field of text, the property itself otherwise, inside
    /// the service's nested form for a property of a nested array. This is the form a lookup that must identify one
    /// record uses, and the form a filter asks each value in.
    /// </summary>
    /// <param name="field">The property, as the schema of the kind searched says it is indexed.</param>
    /// <param name="value">The whole value the property must equal: for a number its digits, for a boolean true or false.</param>
    /// <param name="caseInsensitive">
    /// Ask the <c>keywordLower</c> sub-field of a text property instead. Only where the platform enables it: on a
    /// platform that does not, the sub-field does not exist and the query matches nothing.
    /// </param>
    /// <exception cref="OsduQueryException">The value cannot be asked for exactly, and the message says which rule refuses it.</exception>
    public static OsduQuery Equal(OsduField field, string value, bool caseInsensitive = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        var term = $"{ComparedPath(field, caseInsensitive)}:{LuceneText.Phrase(CheckEqual(field, value, caseInsensitive))}";
        return field.NestedPath is { } nested ? Nested(nested, new OsduQuery(term, [value])) : new OsduQuery(term, [value]);
    }

    /// <summary>
    /// Why <paramref name="value"/> cannot be asked for exactly in <paramref name="field"/>, or null when it can: the rule
    /// <see cref="Equal"/> would refuse it by, without building the query. A filter that holds many values asks this of
    /// each, so one value no query can carry is set aside rather than failing the rest.
    /// </summary>
    public static string? EqualProblem(OsduField field, string value)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            _ = Equal(field, value);
            return null;
        }
        catch (OsduQueryException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Finds the records whose <paramref name="field"/> is any one of <paramref name="values"/>, each compared exactly as
    /// <see cref="Equal"/> compares it. Outside a nested array the values are one grouped comparison of the field,
    /// <c>field:("a" OR "b")</c>; inside one each value is a nested query of its own and the queries are OR'd, because the
    /// service rewrites what follows <c>OR</c> inside the nested form as a property name, which a value such as an OSDU id
    /// (<c>osdu:reference-data--X:1:</c>) would be taken for. A value given twice is asked once.
    /// </summary>
    /// <remarks>
    /// Every value is one clause of the query. The service allows 1024 clauses in a query (the search API document), which
    /// the caller keeps to by asking for as many values at once as leave room for whatever the query is combined with.
    /// </remarks>
    /// <exception cref="OsduQueryException">No value was given, or one cannot be asked for exactly; the message names it.</exception>
    public static OsduQuery AnyOf(OsduField field, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(values);
        var distinct = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value, nameof(values));
            if (seen.Add(value))
            {
                distinct.Add(value);
            }
        }

        if (distinct.Count == 0)
        {
            throw new OsduQueryException(
                $"'{field.Path}' is compared with no value; a query that should match every record is written as '*' by the caller that means it.");
        }

        if (distinct.Count == 1)
        {
            return Equal(field, distinct[0]);
        }

        if (field.NestedPath is not null)
        {
            return Any(distinct.Select(v => Equal(field, v)).ToArray());
        }

        var phrases = distinct.Select(v => LuceneText.Phrase(CheckEqual(field, v, caseInsensitive: false)));
        return new OsduQuery($"{ComparedPath(field, caseInsensitive: false)}:({string.Join(" OR ", phrases)})", compared: null);
    }

    /// <summary>
    /// Finds the records whose <paramref name="field"/> holds a value from <paramref name="from"/> (included) up to
    /// <paramref name="to"/> (left out), compared the way the index orders the field: text by its keyword sub-field, in
    /// the order of its characters' code points, which is how Elasticsearch orders the UTF-8 bytes of a term; a number,
    /// a boolean and a date by the value. A null bound leaves that end open.
    /// </summary>
    /// <exception cref="OsduQueryException">Both ends are open, or a bound cannot be carried in a query.</exception>
    public static OsduQuery Range(OsduField field, string? from, string? to)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (from is null && to is null)
        {
            throw new OsduQueryException(
                $"a range of '{field.Path}' open at both ends asks for every value; leave the range out instead.");
        }

        var bounds = new List<string>(2);
        foreach (var bound in new[] { from, to })
        {
            if (bound is not null)
            {
                // A bound is a position in the field's order, not a value a record must hold, so text of any length and the
                // text null are positions like any other; a number, a boolean or a date has to be one of those.
                CheckValue(field.Path, bound, "range bound");
                CheckTyped(field, bound);
                bounds.Add(bound);
            }
        }

        var lower = from is null ? "*" : LuceneText.Phrase(from);
        var upper = to is null ? "*]" : LuceneText.Phrase(to) + "}";
        var term = $"{field.ExactPath}:[{lower} TO {upper}";
        return field.NestedPath is { } nested ? Nested(nested, new OsduQuery(term, bounds)) : new OsduQuery(term, bounds);
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
        return new OsduQuery($"{path}:{LuceneText.Phrase(value)}", [value]);
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
        if (inner.Compared is not { } values)
        {
            throw new OsduQueryException(
                $"a nested query over '{parent}' holds one comparison: the search service rewrites the property names inside the nested form by pattern, and a group of comparisons does not survive that rewriting.");
        }

        foreach (var value in values)
        {
            if (ServiceParser.NestedQueryProblem(value) is { } problem)
            {
                throw new OsduQueryException($"the value compared inside the nested array '{parent}' cannot be asked for: {problem}.");
            }
        }

        return new OsduQuery($"nested({parent}, ({inner.Text}))", compared: null);
    }

    /// <summary>
    /// Finds the records holding one object of the nested array <paramref name="path"/> whose properties equal every value
    /// given, each compared as <see cref="Equal"/> compares it: <c>nested(data.GeoContexts, (GeoPoliticalEntityID.keyword:"x"
    /// AND GeoTypeID.keyword:"y"))</c>, all of them in the same object. The comparisons are joined flat by <c>AND</c>, with no
    /// parentheses of their own, which is the form the service's parser reads one property at a time: it prefixes the first
    /// property with the array's path, and each one that follows an <c>AND</c> (<see cref="ServiceParser"/>). One comparison
    /// is the same as <see cref="Equal"/>'s.
    /// </summary>
    /// <exception cref="OsduQueryException">
    /// No comparison was given, one is of a property outside the array, or a value cannot be asked for exactly or carried
    /// inside a nested query; the message names it.
    /// </exception>
    public static OsduQuery NestedAll(string path, IReadOnlyList<(OsduField Field, string Value)> comparisons)
    {
        var parent = OsduPath.Of(path);
        ArgumentNullException.ThrowIfNull(comparisons);
        if (comparisons.Count == 0)
        {
            throw new OsduQueryException($"a nested query over '{parent}' compares at least one property.");
        }

        var terms = new List<string>(comparisons.Count);
        foreach (var (field, value) in comparisons)
        {
            ArgumentNullException.ThrowIfNull(field);
            ArgumentNullException.ThrowIfNull(value);
            if (!string.Equals(field.NestedPath, parent, StringComparison.Ordinal))
            {
                throw new OsduQueryException($"'{field.Path}' is not a property of the nested array '{parent}', so it cannot be compared inside it.");
            }

            var checkedValue = CheckEqual(field, value, caseInsensitive: false);
            if (ServiceParser.NestedQueryProblem(checkedValue) is { } problem)
            {
                throw new OsduQueryException($"the value compared with '{field.Path}' inside the nested array '{parent}' cannot be asked for: {problem}.");
            }

            terms.Add($"{ComparedPath(field, caseInsensitive: false)}:{LuceneText.Phrase(checkedValue)}");
        }

        return new OsduQuery($"nested({parent}, ({string.Join(" AND ", terms)}))", compared: null);
    }

    /// <summary>Every one of <paramref name="queries"/> must hold. One query is itself; none is refused.</summary>
    /// <exception cref="OsduQueryException">No query was given.</exception>
    public static OsduQuery All(params OsduQuery[] queries) => Join("AND", queries);

    /// <summary>Any of <paramref name="queries"/> may hold. One query is itself; none is refused.</summary>
    /// <exception cref="OsduQueryException">No query was given.</exception>
    public static OsduQuery Any(params OsduQuery[] queries) => Join("OR", queries);

    /// <summary>The field a comparison of <paramref name="field"/> asks, relative to its nested array when it has one.</summary>
    private static string ComparedPath(OsduField field, bool caseInsensitive)
        => caseInsensitive && field.Index == OsduFieldIndex.Text ? $"{field.QueryPath}.{KeywordLowerSubField}" : field.ExactPath;

    /// <summary>Checks a value an exact comparison of <paramref name="field"/> asks for, and returns the text the query carries.</summary>
    private static string CheckEqual(OsduField field, string value, bool caseInsensitive)
    {
        var path = field.Path;
        CheckValue(path, value, "value");
        switch (field.Index)
        {
            case OsduFieldIndex.Text:
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

                return value;
            case OsduFieldIndex.Keyword:
                RefuseCaseInsensitive(field, caseInsensitive);
                var bytes = Encoding.UTF8.GetByteCount(value);
                if (bytes > MaxTermBytes)
                {
                    throw new OsduQueryException(
                        $"the value compared with '{path}' is {bytes} bytes of UTF-8, and an indexed term holds at most {MaxTermBytes}, so no record can hold it.");
                }

                return value;
            default:
                RefuseCaseInsensitive(field, caseInsensitive);
                CheckTyped(field, value);
                return value;
        }
    }

    /// <summary>
    /// Refuses text that is not a value of a number, boolean or date field: Elasticsearch parses a compared phrase as the
    /// field's type and answers 400 for one it cannot parse, which would fail the whole query rather than match nothing.
    /// </summary>
    private static void CheckTyped(OsduField field, string value)
    {
        switch (field.Index)
        {
            case OsduFieldIndex.Number:
                if (!double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number))
                {
                    throw new OsduQueryException($"'{field.Path}' is indexed as a number, and '{value}' is not one.");
                }

                break;
            case OsduFieldIndex.Boolean:
                if (value is not ("true" or "false"))
                {
                    throw new OsduQueryException($"'{field.Path}' is indexed as a boolean, and '{value}' is neither true nor false.");
                }

                break;
            case OsduFieldIndex.Date:
                if (value.Trim().Length != value.Length
                    || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                {
                    throw new OsduQueryException($"'{field.Path}' is indexed as a date, and '{value}' is not one.");
                }

                break;
        }
    }

    private static void RefuseCaseInsensitive(OsduField field, bool caseInsensitive)
    {
        if (caseInsensitive)
        {
            throw new OsduQueryException(
                $"'{field.Path}' is indexed as a {OsduField.Describe(field.Index)}, which has no '{KeywordLowerSubField}' sub-field, so it cannot be compared regardless of case.");
        }
    }

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
            : new OsduQuery(string.Join($" {op} ", queries.Select(q => $"({q.Text})")), compared: null);
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
