using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Delivery.Identity;

/// <summary>
/// RFC 4122 name-based UUID (version 5, SHA-1). Used for the delivery key and the flow id so both halves of the
/// system derive the same identity from the same data with no coordination (design.md sections 5.2 and 12.1).
/// </summary>
public static class DeterministicGuid
{
    /// <summary>Root namespace for everything this project mints. Never change it: it re-keys every ledger row.</summary>
    public static readonly Guid OsduDeliveryNamespace = new("6b6d1c3e-3a3c-5d0a-9f76-0f4a2c1e8d21");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 4122 version 5 UUIDs are defined over SHA-1; the hash is an identity function here, not a security control.")]
    public static Guid V5(Guid ns, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var nsBytes = ns.ToByteArray();
        SwapByteOrder(nsBytes);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[16 + nameBytes.Length];
        nsBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        var guidBytes = hash[..16].ToArray();
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        SwapByteOrder(guidBytes);
        return new Guid(guidBytes);
    }

    /// <summary>Derives a child namespace from the root, so distinct concerns can never collide.</summary>
    public static Guid Namespace(string concern) => V5(OsduDeliveryNamespace, concern);

    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
