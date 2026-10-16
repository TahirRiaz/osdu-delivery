using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Search;

/// <summary>
/// A property path inside an OSDU record, as a query names it: <c>data.FacilityName</c>, or <c>AliasName</c> relative
/// to a nested path.
/// </summary>
/// <remarks>
/// A path goes into the query unquoted, before the colon, so unlike a value it cannot be escaped into safety: a path
/// carrying a space, a colon or a parenthesis would be read as query syntax and ask something other than what was
/// meant. It is checked instead, and a path that cannot be written plainly is refused where it is written rather than
/// where it silently matches nothing.
/// </remarks>
public static partial class OsduPath
{
    /// <summary>The longest path accepted, which is well past any OSDU schema and keeps a refusal message readable.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// <paramref name="path"/> checked as a property path, returned as written.
    /// </summary>
    /// <exception cref="OsduQueryException">The path is empty, too long, or holds something a query would read as syntax.</exception>
    public static string Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            throw new OsduQueryException("a query names no property to match on.");
        }

        if (path.Length > MaxLength)
        {
            throw new OsduQueryException($"the property path is {path.Length} characters; a path is at most {MaxLength}.");
        }

        if (!PathPattern().IsMatch(path))
        {
            throw new OsduQueryException(
                $"'{path}' cannot be written in a query as a property path. A path is segments of letters, digits and underscores separated by dots, such as data.FacilityName. A path goes into the query unquoted, so anything else would be read as query syntax.");
        }

        return path;
    }

    /// <summary>Whether <paramref name="path"/> can be written in a query, without throwing.</summary>
    public static bool IsPath(string? path)
        => path is { Length: > 0 and <= MaxLength } && PathPattern().IsMatch(path);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex PathPattern();
}
