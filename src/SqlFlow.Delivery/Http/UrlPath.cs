using System.Text;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Escaping for values that go into a URL <em>path segment</em>, which is not the same thing as escaping for a
/// query string.
///
/// OSDU identifiers are colon separated by construction (<c>opendes:master-data--Well:1234</c>), every storage,
/// file and DDMS endpoint takes one in the path, and a search cursor is base64 and carries <c>=</c>. RFC 3986
/// allows all of those unescaped in a segment: <c>pchar = unreserved / pct-encoded / sub-delims / ":" / "@"</c>.
/// <see cref="Uri.EscapeDataString(string)"/> targets <c>application/x-www-form-urlencoded</c> instead and percent-encodes
/// them anyway, which leaves a request that only works when whatever sits in front of the service decodes the path
/// before matching it. Every reference OSDU client sends the colons raw, so this does too: it escapes what a
/// segment genuinely cannot carry (the delimiters <c>/ ? #</c>, a literal <c>%</c>, whitespace, control characters
/// and anything non-ASCII) and leaves the rest alone.
/// </summary>
public static class UrlPath
{
    /// <summary>The value as one path segment, percent-encoding only what a segment cannot carry literally.</summary>
    public static string EscapeSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return value;
        }

        var needed = false;
        foreach (var c in value)
        {
            if (!IsSegmentSafe(c))
            {
                needed = true;
                break;
            }
        }

        if (!needed)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 16);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && IsSegmentSafe((char)rune.Value))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            var written = rune.EncodeToUtf8(utf8);
            for (var i = 0; i < written; i++)
            {
                builder.Append('%').Append(utf8[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>True for a character RFC 3986 allows literally in a path segment (pchar, minus the pct-encode marker).</summary>
    private static bool IsSegmentSafe(char c)
        => c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            // unreserved
            or '-' or '.' or '_' or '~'
            // sub-delims
            or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '='
            // pchar additions
            or ':' or '@';
}
