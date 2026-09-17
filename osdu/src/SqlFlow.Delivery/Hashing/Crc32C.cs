using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SqlFlow.Delivery.Hashing;

/// <summary>
/// CRC-32C (Castagnoli), the checksum Google Cloud Storage keeps for every object (its <c>crc32c</c>, the checksum
/// Seismic Store's client compares after an upload, osdu/specs/seismic-ddms/INTEGRATION.md section 4.4). Computed with the
/// processor's CRC32 instruction where there is one.
/// </summary>
public static class Crc32C
{
    /// <summary>The checksum of <paramref name="data"/> following <paramref name="crc"/>, the checksum of what came before it (0 for nothing).</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var state = ~crc;
        var words = MemoryMarshal.Cast<byte, ulong>(data[..(data.Length - (data.Length % sizeof(ulong)))]);
        foreach (var word in words)
        {
            state = BitOperations.Crc32C(state, BitConverter.IsLittleEndian ? word : BinaryPrimitives.ReverseEndianness(word));
        }

        foreach (var b in data[(words.Length * sizeof(ulong))..])
        {
            state = BitOperations.Crc32C(state, b);
        }

        return ~state;
    }

    /// <summary>A checksum as Google Cloud Storage writes it: its four bytes, most significant first, in base64.</summary>
    public static string ToBase64(uint crc)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, crc);
        return Convert.ToBase64String(bytes);
    }
}
