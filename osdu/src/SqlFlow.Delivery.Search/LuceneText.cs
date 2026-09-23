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
/// <c>data.FacilityName:NO 15/9-A-1</c> asks for records matching <c>NO</c> or <c>15/9-A-1</c>, which is most of a
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
    /// Whether <paramref name="value"/> holds a character that cannot survive a query at all: a control character, which
    /// the parser neither escapes nor carries, so a value holding one can never match what is indexed.
    /// </summary>
    public static bool HasUnquotableCharacter(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Any(char.IsControl);
    }
}
