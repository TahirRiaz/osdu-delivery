using System.Globalization;
using System.Text;

namespace SqlFlow.Delivery.Identity;

/// <summary>
/// How text is written into the code segment of an OSDU id (<c>{partition}:{entityType}:{code}</c>), and read back out of
/// one. The storage service takes ids matching <c>^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+$</c> (openapi storage v2, the
/// record id), so ASCII letters, digits, <c>_ - . :</c> are written as they stand and every other character is
/// percent-encoded as its UTF-8 bytes, with upper-case hex digits (<c>g/cm3</c> becomes <c>g%2Fcm3</c>, <c>%</c> becomes
/// <c>%25</c>). This is the one encoder: an id modifier's tokens and a record id derived from its key both go through it.
/// </summary>
public static class IdSegment
{
    /// <summary>Whether <paramref name="c"/> is written into an id as it stands: an ASCII letter or digit, '_', '-', '.' or ':'.</summary>
    public static bool IsIdCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    /// <summary>
    /// <paramref name="value"/> as an id carries it, or null when it is not valid Unicode text (a lone surrogate), which no
    /// id can carry.
    /// </summary>
    /// <param name="value">The text written.</param>
    /// <param name="keepEscapes">
    /// True keeps a percent-escape already in the value as it stands, its hex digits upper-cased, so a code written encoded is
    /// not encoded twice: what a reference built from a value needs. False writes every '%' as <c>%25</c>, so two different
    /// values never give one segment: what an id that stands for a record's identity needs.
    /// </param>
    /// <param name="keepColons">
    /// True writes ':' as it stands, since ':' parts the code of an id such as <c>LIS-LAS::GAPI</c> or a CRS's
    /// <c>Projected:EPSG::23031</c>. False writes it as <c>%3A</c>, for a value that is one of several joined by ':'.
    /// </param>
    public static string? Encode(string value, bool keepEscapes, bool keepColons = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        var written = new StringBuilder(value.Length);
        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < value.Length;)
        {
            var c = value[i];
            if (IsIdCharacter(c) && (keepColons || c != ':'))
            {
                written.Append(c);
                i++;
                continue;
            }

            if (keepEscapes && c == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                written.Append('%').Append(char.ToUpperInvariant(value[i + 1])).Append(char.ToUpperInvariant(value[i + 2]));
                i += 3;
                continue;
            }

            if (Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return null;
            }

            var length = rune.EncodeToUtf8(bytes);
            for (var b = 0; b < length; b++)
            {
                written.Append('%').Append(bytes[b].ToString("X2", CultureInfo.InvariantCulture));
            }

            i += consumed;
        }

        return written.ToString();
    }

    /// <summary>
    /// The text a segment stands for: every percent-escape read back as UTF-8, the rest as it stands. Null when the segment
    /// holds a character an id never carries, an escape that is not '%' and two hex digits, or bytes that are not UTF-8.
    /// It reverses <see cref="Encode"/> without kept escapes: decoding what that wrote gives the value back.
    /// </summary>
    public static string? Decode(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var bytes = new List<byte>(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '%')
            {
                if (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2]))
                {
                    return null;
                }

                bytes.Add(byte.Parse(segment.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
                continue;
            }

            if (!IsIdCharacter(c))
            {
                return null;
            }

            bytes.Add((byte)c);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
