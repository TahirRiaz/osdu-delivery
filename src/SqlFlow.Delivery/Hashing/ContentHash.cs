using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Delivery.Hashing;

/// <summary>
/// SHA-256 over canonical bytes, rendered as lower-case hex. One helper so every hash in the system is computed the
/// same way and can be compared as a plain string.
/// </summary>
public static class ContentHash
{
    public const int HexLength = 64;

    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Of(string text) => Of(Encoding.UTF8.GetBytes(text));

    /// <summary>Hashes several parts as one stream with a length prefix per part, so concatenation cannot collide.</summary>
    public static string OfParts(params string[] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> len = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
            sha.AppendData(len);
            sha.AppendData(bytes);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static string OfStream(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}
