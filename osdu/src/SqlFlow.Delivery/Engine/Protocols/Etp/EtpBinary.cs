using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// A message that does not follow the pinned ETP schema (osdu/specs/reservoir-ddms/etp-1.2.avpr): a truncated body, a
/// length or block count the message cannot hold, a union branch or enum symbol the schema does not define. It is a
/// delivery failure like any other, so the attempt is recorded with it.
/// </summary>
public sealed class EtpFormatException : DeliveryException
{
    public EtpFormatException(string message)
        : base(message)
    {
    }

    public EtpFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Writes Avro binary as ETP 1.2 frames carry it: no container file and no schema on the wire, because both ends hold
/// the pinned protocol (osdu/specs/reservoir-ddms/INTEGRATION.md section 2.4). The buffer is rented, so
/// <see cref="Written"/> is only valid until <see cref="Dispose"/>; copy what outlives the writer.
/// </summary>
public sealed class EtpWriter : IDisposable
{
    /// <summary>The ETP server's own default wire limit, and this writer's when a session has not negotiated one.</summary>
    public const int DefaultCeilingBytes = 16_000_000;

    private const int InitialBytes = 4096;

    private readonly int _ceiling;
    private byte[] _buffer;
    private int _written;

    public EtpWriter(int ceilingBytes = DefaultCeilingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ceilingBytes, 64);
        _ceiling = ceilingBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(ceilingBytes, InitialBytes));
    }

    /// <summary>Bytes written so far. Valid until the writer is disposed or written to again.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _written);

    public int Length => _written;

    /// <summary>The most bytes this writer will produce: the session's negotiated size, or the default.</summary>
    public int CeilingBytes => _ceiling;

    /// <summary>Forgets what was written, keeping the buffer, so one writer can frame message after message.</summary>
    public void Reset() => _written = 0;

    public void WriteBoolean(bool value)
    {
        Reserve(1)[0] = value ? (byte)1 : (byte)0;
        _written += 1;
    }

    public void WriteInt(int value)
    {
        var zigZag = (uint)((value << 1) ^ (value >> 31));
        var span = Reserve(5);
        var length = 0;
        while (zigZag >= 0x80)
        {
            span[length++] = (byte)(zigZag | 0x80);
            zigZag >>= 7;
        }

        span[length++] = (byte)zigZag;
        _written += length;
    }

    public void WriteLong(long value)
    {
        var zigZag = (ulong)((value << 1) ^ (value >> 63));
        var span = Reserve(10);
        var length = 0;
        while (zigZag >= 0x80)
        {
            span[length++] = (byte)(zigZag | 0x80);
            zigZag >>= 7;
        }

        span[length++] = (byte)zigZag;
        _written += length;
    }

    public void WriteFloat(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);
        _written += 4;
    }

    public void WriteDouble(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
        _written += 8;
    }

    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var length = Encoding.UTF8.GetByteCount(value);
        WriteLong(length);
        if (length == 0)
        {
            return;
        }

        Encoding.UTF8.GetBytes(value, Reserve(length));
        _written += length;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteLong(value.Length);
        if (value.IsEmpty)
        {
            return;
        }

        value.CopyTo(Reserve(value.Length));
        _written += value.Length;
    }

    /// <summary>An Avro fixed: its bytes as they stand, with no length in front of them.</summary>
    public void WriteFixed(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        value.CopyTo(Reserve(value.Length));
        _written += value.Length;
    }

    /// <summary>A UUID in the RFC 4122 order ETP sends, which is not the order .NET lays a <see cref="Guid"/> out in.</summary>
    public void WriteUuid(Guid value)
    {
        if (!value.TryWriteBytes(Reserve(16), bigEndian: true, out _))
        {
            throw new EtpFormatException("A UUID could not be written into 16 bytes.");
        }

        _written += 16;
    }

    public void WriteEnum(int ordinal) => WriteInt(ordinal);

    /// <summary>The branch of a union, written before the branch's own value.</summary>
    public void WriteUnion(int branch) => WriteLong(branch);

    /// <summary>
    /// The count of one array or map block. A count of zero writes nothing, so an empty array or map is the single
    /// terminator <see cref="WriteBlockEnd"/> writes.
    /// </summary>
    public void WriteBlockHeader(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > 0)
        {
            WriteLong(count);
        }
    }

    /// <summary>The zero that ends an array or a map.</summary>
    public void WriteBlockEnd() => WriteLong(0);

    public void WriteBooleanArray(ReadOnlyMemory<bool> values)
    {
        WriteBlockHeader(values.Length);
        var source = values.Span;
        if (!source.IsEmpty)
        {
            var target = Reserve(source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                target[i] = source[i] ? (byte)1 : (byte)0;
            }

            _written += source.Length;
        }

        WriteBlockEnd();
    }

    public void WriteIntArray(ReadOnlyMemory<int> values)
    {
        WriteBlockHeader(values.Length);
        var source = values.Span;
        for (var i = 0; i < source.Length; i++)
        {
            WriteInt(source[i]);
        }

        WriteBlockEnd();
    }

    public void WriteLongArray(ReadOnlyMemory<long> values)
    {
        WriteBlockHeader(values.Length);
        var source = values.Span;
        for (var i = 0; i < source.Length; i++)
        {
            WriteLong(source[i]);
        }

        WriteBlockEnd();
    }

    public void WriteFloatArray(ReadOnlyMemory<float> values)
    {
        WriteBlockHeader(values.Length);
        WriteLittleEndian(MemoryMarshal.AsBytes(values.Span), values.Span.Length, sizeof(float));
        WriteBlockEnd();
    }

    public void WriteDoubleArray(ReadOnlyMemory<double> values)
    {
        WriteBlockHeader(values.Length);
        WriteLittleEndian(MemoryMarshal.AsBytes(values.Span), values.Span.Length, sizeof(double));
        WriteBlockEnd();
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Avro writes floats and doubles little-endian, which is the machine layout on every platform this runs on;
    /// elsewhere each element is reversed into place, so the wire is the same either way.
    /// </summary>
    private void WriteLittleEndian(ReadOnlySpan<byte> source, int count, int elementSize)
    {
        if (source.IsEmpty)
        {
            return;
        }

        var target = Reserve(source.Length);
        if (BitConverter.IsLittleEndian)
        {
            source.CopyTo(target);
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                var element = source.Slice(i * elementSize, elementSize);
                for (var b = 0; b < elementSize; b++)
                {
                    target[(i * elementSize) + b] = element[elementSize - 1 - b];
                }
            }
        }

        _written += source.Length;
    }

    /// <summary>
    /// Room for <paramref name="count"/> more bytes, without counting them as written: the caller adds what it used,
    /// which is what lets a varint reserve its worst case and write fewer.
    /// </summary>
    private Span<byte> Reserve(int count)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        if (_buffer.Length - _written < count)
        {
            Grow(count);
        }

        return _buffer.AsSpan(_written, count);
    }

    private void Grow(int count)
    {
        var needed = (long)_written + count;
        if (needed > _ceiling)
        {
            throw new DeliveryException(
                $"An ETP message reached {needed} bytes, past the {_ceiling} bytes this session may send. Send fewer objects or arrays per message, or slice the array.");
        }

        var size = Math.Min((long)_ceiling, Math.Max(needed, Math.Min((long)_buffer.Length * 2, _ceiling)));
        var grown = ArrayPool<byte>.Shared.Rent((int)size);
        _buffer.AsSpan(0, _written).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}

/// <summary>
/// Reads Avro binary as ETP 1.2 frames carry it. Every length, block count, union branch and enum symbol is checked
/// against what the buffer can hold before anything is allocated, so a malformed or hostile message fails with
/// <see cref="EtpFormatException"/> rather than exhausting memory.
/// </summary>
public ref struct EtpReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _at;

    public EtpReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _at = 0;
    }

    public readonly int Position => _at;

    public readonly int Remaining => _data.Length - _at;

    public readonly bool AtEnd => _at >= _data.Length;

    public bool ReadBoolean()
    {
        var value = Take(1)[0];
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new EtpFormatException($"A boolean at byte {_at - 1} of an ETP message is {value}, which is neither 0 nor 1."),
        };
    }

    public int ReadInt()
    {
        var value = ReadLong();
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new EtpFormatException($"An int in an ETP message is {value}, which does not fit in 32 bits.");
        }

        return (int)value;
    }

    public long ReadLong()
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            if (shift > 63)
            {
                throw new EtpFormatException($"A variable-length integer at byte {_at} of an ETP message runs past 64 bits.");
            }

            var part = Take(1)[0];
            value |= (ulong)(part & 0x7F) << shift;
            if ((part & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    public float ReadFloat() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public string ReadString()
    {
        var length = Length("string");
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(Take(length));
    }

    public ReadOnlyMemory<byte> ReadBytes()
    {
        var length = Length("byte string");
        return length == 0 ? ReadOnlyMemory<byte>.Empty : Take(length).ToArray();
    }

    /// <summary>An Avro fixed: the next <paramref name="size"/> bytes of the message as they stand.</summary>
    public ReadOnlySpan<byte> ReadFixed(int size) => Take(size);

    /// <summary>A UUID in the RFC 4122 order ETP sends, which is not the order .NET lays a <see cref="Guid"/> out in.</summary>
    public Guid ReadUuid() => new(Take(16), bigEndian: true);

    /// <summary>One enum symbol, refused when it is not one of the <paramref name="symbols"/> the schema defines.</summary>
    public int ReadEnum(int symbols)
    {
        var ordinal = ReadInt();
        if (ordinal < 0 || ordinal >= symbols)
        {
            throw new EtpFormatException($"An enum in an ETP message is symbol {ordinal}, and the pinned schema defines {symbols}.");
        }

        return ordinal;
    }

    /// <summary>One union branch, refused when it is not one of the <paramref name="branches"/> the schema defines.</summary>
    public int ReadUnion(int branches)
    {
        var branch = ReadLong();
        if (branch < 0 || branch >= branches)
        {
            throw new EtpFormatException($"A union in an ETP message selects branch {branch}, and the pinned schema defines {branches}.");
        }

        return (int)branch;
    }

    /// <summary>
    /// The item count of the next array or map block, or zero at the end of one. A negative count carries the block's
    /// byte size, which is read and dropped. No item of the pinned schema encodes in nothing, so a count past the bytes
    /// left is a malformed message and never an allocation.
    /// </summary>
    public int ReadBlockCount()
    {
        var count = ReadLong();
        if (count < 0)
        {
            if (count == long.MinValue)
            {
                throw new EtpFormatException("A block count in an ETP message cannot be negated.");
            }

            count = -count;
            _ = ReadLong();
        }

        if (count > Remaining)
        {
            throw new EtpFormatException($"A block of an ETP message declares {count} items with {Remaining} bytes left to read.");
        }

        return (int)count;
    }

    public ReadOnlyMemory<bool> ReadBooleanArray()
    {
        var values = new List<bool>();
        for (var count = ReadBlockCount(); count != 0; count = ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + count);
            for (var i = 0; i < count; i++)
            {
                values.Add(ReadBoolean());
            }
        }

        return values.ToArray();
    }

    public ReadOnlyMemory<int> ReadIntArray()
    {
        var values = new List<int>();
        for (var count = ReadBlockCount(); count != 0; count = ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + count);
            for (var i = 0; i < count; i++)
            {
                values.Add(ReadInt());
            }
        }

        return values.ToArray();
    }

    public ReadOnlyMemory<long> ReadLongArray()
    {
        var values = new List<long>();
        for (var count = ReadBlockCount(); count != 0; count = ReadBlockCount())
        {
            values.EnsureCapacity(values.Count + count);
            for (var i = 0; i < count; i++)
            {
                values.Add(ReadLong());
            }
        }

        return values.ToArray();
    }

    public ReadOnlyMemory<float> ReadFloatArray()
    {
        var values = new List<float>();
        for (var count = ReadBlockCount(); count != 0; count = ReadBlockCount())
        {
            Fixed(count, sizeof(float));
            values.EnsureCapacity(values.Count + count);
            for (var i = 0; i < count; i++)
            {
                values.Add(ReadFloat());
            }
        }

        return values.ToArray();
    }

    public ReadOnlyMemory<double> ReadDoubleArray()
    {
        var values = new List<double>();
        for (var count = ReadBlockCount(); count != 0; count = ReadBlockCount())
        {
            Fixed(count, sizeof(double));
            values.EnsureCapacity(values.Count + count);
            for (var i = 0; i < count; i++)
            {
                values.Add(ReadDouble());
            }
        }

        return values.ToArray();
    }

    /// <summary>A length prefix, refused when the message cannot hold that many bytes.</summary>
    private int Length(string what)
    {
        var length = ReadLong();
        if (length < 0 || length > Remaining)
        {
            throw new EtpFormatException($"A {what} in an ETP message declares {length} bytes with {Remaining} bytes left to read.");
        }

        return (int)length;
    }

    /// <summary>A block of fixed-width elements, refused before the list is sized when the bytes are not there.</summary>
    private readonly void Fixed(int count, int elementSize)
    {
        if ((long)count * elementSize > Remaining)
        {
            throw new EtpFormatException($"A block of an ETP message declares {count} elements of {elementSize} bytes with {Remaining} bytes left to read.");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (Remaining < count)
        {
            throw new EtpFormatException($"An ETP message ended after {_data.Length} bytes while {count} more were expected at byte {_at}.");
        }

        var taken = _data.Slice(_at, count);
        _at += count;
        return taken;
    }
}
