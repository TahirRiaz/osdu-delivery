using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The OSDU record the ETP server registers for a dataspace it creates (osdu/specs/reservoir-ddms/INTEGRATION.md
/// section 6.3). The engine never writes it, but it causes it, so the id is computable before the dataspace is created
/// and is logged when it is, as this project's live-OSDU rule requires of every id a service mints on its behalf.
/// </summary>
public static class EtpDataspaceRecord
{
    /// <summary>The entity type of the record, whose kind the server builds as <c>{schema}:wks:dataset--ETPDataspace:1.0.0</c>.</summary>
    public const string EntityType = "dataset--ETPDataspace";

    /// <summary>The characters the server's URL encoder keeps as they are.</summary>
    private const string Unreserved = "-_.!~*'()";

    /// <summary>Past this length the server hashes the id instead of spelling it out.</summary>
    private const int MaxIdLength = 64;

    /// <summary>The RFC 4122 DNS namespace, which the server's name-based id generator uses.</summary>
    private static readonly byte[] DnsNamespace =
    [
        0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8,
    ];

    /// <summary>
    /// The id of the dataspace's record: <c>{partition}:dataset--ETPDataspace:{urlId}</c>, where the url id is the
    /// dataspace path with every slash replaced by a dash, URL encoded, and every percent sign replaced by an
    /// underscore. A url id past 64 characters becomes its first 32 characters and a name-based UUID of the whole.
    /// </summary>
    public static string Id(Uri endpoint, string path, string? partition)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return $"{partition ?? "unknown-partition"}:{EntityType}:{UrlId(path)}";
    }

    /// <summary>The identifying part of the record's id, from the dataspace path.</summary>
    public static string UrlId(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var encoded = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path.Replace('/', '-')))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || Unreserved.Contains(c, StringComparison.Ordinal))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('_').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        var id = encoded.ToString();
        return id.Length <= MaxIdLength ? id : id[..32] + NameBased(id).ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>A version 5 UUID of <paramref name="name"/> in the DNS namespace, which is what the server's generator makes.</summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 4122 version 5 identifiers are defined over SHA-1; this reproduces the id the ETP server computes, and is not a security decision.")]
    private static Guid NameBased(string name)
    {
        var bytes = new byte[DnsNamespace.Length + Encoding.UTF8.GetByteCount(name)];
        DnsNamespace.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(name, bytes.AsSpan(DnsNamespace.Length));
        var hash = SHA1.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
