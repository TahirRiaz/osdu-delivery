using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Search;

/// <summary>
/// What the search service's own query parser does to a query before Elasticsearch sees it
/// (<c>QueryParserUtil.buildQueryBuilderFromQueryString</c>), and so which values a query cannot carry intact.
/// </summary>
/// <remarks>
/// <para>
/// A query that contains <c>nested(</c> or <c>nested (</c> anywhere, inside a quoted value or not, is taken apart by
/// regular expressions rather than passed to Elasticsearch as it stands. Only the tokenizer honours quotes. The rest
/// works on the raw text:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A value holding <c>nested(</c> itself turns a plain query into one the parser reads as nested syntax, or a nested
/// query into a multi-level one (<c>isMultilevelNestedPattern</c>).
/// </description></item>
/// <item><description>
/// Inside <c>nested(path, (query))</c>, every <c>AND</c>, <c>OR</c> or <c>NOT</c> followed by whitespace and a run of
/// characters ending in a colon is read as the next property of the inner query and prefixed with the array's path
/// (<c>intermediateStringQueryNestedPattern</c>, which has no word boundary: <c>BRAND X:1</c> matches). Inside a value
/// that rewrites the value, and the run is used as a regular expression, so a parenthesis or a dollar sign in it makes
/// the service fail outright.
/// </description></item>
/// <item><description>
/// The closing parentheses of the inner query are balanced by counting every parenthesis, quoted or not
/// (<c>trimTrailingBrackets</c>), so a value whose own parentheses do not balance cuts the inner query short or leaves
/// it with one too many, and the query is malformed.
/// </description></item>
/// </list>
/// <para>
/// A plain query without <c>nested(</c> goes to Elasticsearch whole, where a quoted phrase carries anything the phrase
/// escaping can express.
/// </para>
/// </remarks>
internal static partial class ServiceParser
{
    /// <summary>The text that makes the service read a query as nested syntax, wherever it stands.</summary>
    private static readonly string[] NestedMarkers = ["nested(", "nested ("];

    /// <summary>
    /// Why <paramref name="value"/> cannot be carried in any query, or null when it can. The service scans the whole
    /// query for the nested marker, so a value holding it is read as syntax even inside quotes.
    /// </summary>
    public static string? AnyQueryProblem(string value)
    {
        foreach (var marker in NestedMarkers)
        {
            if (value.Contains(marker, StringComparison.Ordinal))
            {
                return $"it contains '{marker}', which the search service reads as the start of a nested query wherever it appears, inside a quoted value too, so the query it runs would not be the one asked";
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="value"/> cannot be carried inside a nested query, or null when it can: a value the service's
    /// rewriting of the inner query would alter or break.
    /// </summary>
    public static string? NestedQueryProblem(string value)
    {
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth < 0)
            {
                break;
            }
        }

        if (depth != 0)
        {
            return "its parentheses do not balance, and the search service closes a nested query by counting every parenthesis, quoted ones included, so the query would be cut short or left open";
        }

        if (RewrittenAsProperty().Match(value) is { Success: true } match)
        {
            return $"it contains '{match.Value}', which the search service reads inside a nested query as the next property to compare and rewrites, so the value it looks for would not be this one";
        }

        return null;
    }

    /// <summary>
    /// <c>intermediateStringQueryNestedPattern</c> as the service compiles it, <c>(AND|OR|NOT)\s(\S+?):</c>, with
    /// .NET's <c>\s</c>, which matches every whitespace Java's does and more, so nothing the service would rewrite
    /// is let through.
    /// </summary>
    [GeneratedRegex(@"(AND|OR|NOT)\s\S+?:", RegexOptions.CultureInvariant)]
    private static partial Regex RewrittenAsProperty();
}
