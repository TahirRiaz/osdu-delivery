namespace SqlFlow.Delivery.Search;

/// <summary>
/// Text as the OSDU search service's query parser reads it.
/// </summary>
/// <remarks>
/// <para>
/// The service hands the query to Elasticsearch as a <c>query_string</c> query built with <c>escape(false)</c>
/// (<c>search-service</c>, <c>QueryNode.toQueryBuilder</c>), so nothing a caller writes is escaped for it. Every
/// character the syntax reserves is the caller's to escape, and a value carrying one unescaped does not fail: it parses
/// as syntax and silently searches for something else.
/// </para>
/// <para>
/// The same builder sets <c>defaultOperator(OR)</c>, so a bare multi-word value is read as its words OR'd together:
/// <c>data.FacilityName:Wellbore A-1</c> asks for records matching <c>Wellbore</c> or <c>A-1</c>, which is most of a
/// partition. A value is therefore always written as a quoted phrase, never bare.
/// </para>
/// <para>
/// Inside a phrase only the quote and the backslash end or escape it, so a phrase needs only those two escaped. That is
/// the whole rule this type implements, and why it is preferred to escaping the fifteen characters the bare syntax
/// reserves: fewer rules, and none of them depending on where in the query the value lands.
/// </para>
/// </remarks>
public static class LuceneText
{
    /// <summary>
    /// <paramref name="value"/> as a quoted phrase the parser reads as one literal value: the quote and the backslash
    /// escaped, the whole wrapped in quotes.
    /// </summary>
    /// <remarks>
    /// The backslash is escaped first. Escaping the quote first would double-escape the backslash it introduces, so
    /// <c>a"b</c> would become <c>a\\"b</c>, which ends the phrase at the quote and leaves the rest as syntax.
    /// </remarks>
    public static string Phrase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// <paramref name="value"/> as a bare term the parser reads literally, every character its syntax reserves escaped with
    /// a backslash: the form a value takes where a phrase cannot go, which is in front of a wildcard. A prefix search writes
    /// <c>id:</c> and this, then <c>*</c>; a phrase would carry the star as a character to match.
    /// </summary>
    /// <remarks>
    /// The reserved characters are Lucene's classic query parser's (<c>QueryParserBase.escape</c>), which Elasticsearch's
    /// <c>query_string</c> shares, with the slash and the equals sign it adds. Whitespace is escaped too: unescaped, it ends
    /// the term and the rest is OR'd in as a term of its own (the service's default operator). The angle brackets cannot be
    /// escaped at all (Elasticsearch, <c>query_string</c> reserved characters), so a value holding one is refused rather
    /// than sent as a range it never meant (<see cref="IsEscapable"/>).
    /// </remarks>
    /// <exception cref="ArgumentException">The value holds an angle bracket or a control character.</exception>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsEscapable(value))
        {
            throw new ArgumentException("A bare term cannot carry an angle bracket or a control character; ask for such a value as a phrase.", nameof(value));
        }

        var escaped = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (Reserved.Contains(c) || char.IsWhiteSpace(c))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Whether <paramref name="value"/> can be written as a bare term (<see cref="Escape"/>): it holds no angle bracket,
    /// which the parser reads as a range whatever escapes it, and no control character.
    /// </summary>
    public static bool IsEscapable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return !value.Any(c => c is '<' or '>' || char.IsControl(c));
    }

    /// <summary>The characters a bare term escapes (<see cref="Escape"/>).</summary>
    private const string Reserved = "\\+-!():^[]\"{}~*?|&/=";

    /// <summary>
    /// Whether <paramref name="value"/> holds a character that cannot survive a query at all: a control character, which
    /// the parser neither escapes nor carries, so a value holding one can never match what is indexed.
    /// </summary>
    public static bool HasUnquotableCharacter(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Any(char.IsControl);
    }
}
