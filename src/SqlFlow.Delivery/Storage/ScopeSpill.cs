using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// A disk-backed hash partition of scope rows keyed by the delivery key (design.md section 16.1). A drop whose
/// child scopes are not co-partitioned with its root scope cannot be joined in one pass without holding every
/// child row in memory, which does not survive millions of records; instead every row of every scope is appended
/// to one of N bucket files by the hash of its key, and the join then runs bucket by bucket, holding one bucket's
/// children at a time. Bucket files live under a temporary directory that is deleted with the spill. The format
/// is a compact binary framing of <see cref="SourceRow"/> values; nothing is ever parsed as JSON.
/// </summary>
internal sealed class ScopeSpill : IAsyncDisposable
{
    /// <summary>Source parquet bytes per bucket; the in-memory footprint of a bucket is a small multiple of it.</summary>
    public const long TargetBucketBytes = 32L * 1024 * 1024;

    public const int MaxBuckets = 1024;

    private const int BufferBytes = 64 * 1024;

    private readonly string _directory;
    private readonly Dictionary<(string Scope, int Bucket), MemoryStream> _buffers = new();

    public ScopeSpill(int buckets)
    {
        Buckets = Math.Clamp(buckets, 1, MaxBuckets);
        _directory = Path.Combine(Path.GetTempPath(), "osdu-delivery-spill", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public int Buckets { get; }

    /// <summary>How many buckets a set of child files needs, from their total size.</summary>
    public static int BucketsFor(long childBytes)
        => (int)Math.Clamp((childBytes + TargetBucketBytes - 1) / TargetBucketBytes, 1, MaxBuckets);

    public int BucketOf(Guid key)
    {
        // The delivery key is a UUID: its bytes are already well mixed, so a slice of them spreads evenly.
        Span<byte> bytes = stackalloc byte[16];
        key.TryWriteBytes(bytes);
        var hash = BitConverter.ToUInt32(bytes[..4]) ^ BitConverter.ToUInt32(bytes[12..]);
        return (int)(hash % (uint)Buckets);
    }

    public async Task WriteAsync(string scope, Guid key, SourceRow row, CancellationToken ct)
    {
        var bucket = BucketOf(key);
        if (!_buffers.TryGetValue((scope, bucket), out var buffer))
        {
            buffer = new MemoryStream();
            _buffers[(scope, bucket)] = buffer;
        }

        RowCodec.Write(buffer, key, row);
        if (buffer.Length >= BufferBytes)
        {
            await FlushAsync(scope, bucket, buffer, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes every buffered row to its bucket file. Call once after the last write, before reading.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        foreach (var ((scope, bucket), buffer) in _buffers.ToList())
        {
            if (buffer.Length > 0)
            {
                await FlushAsync(scope, bucket, buffer, ct).ConfigureAwait(false);
            }
        }

        _buffers.Clear();
    }

    public async IAsyncEnumerable<(Guid Key, SourceRow Row)> ReadAsync(string scope, int bucket, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = BucketPath(scope, bucket);
        if (!File.Exists(path))
        {
            yield break;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            yield return RowCodec.Read(reader);
        }
    }

    private async Task FlushAsync(string scope, int bucket, MemoryStream buffer, CancellationToken ct)
    {
        await using (var file = new FileStream(BucketPath(scope, bucket), FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await file.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct).ConfigureAwait(false);
        }

        buffer.SetLength(0);
    }

    private string BucketPath(string scope, int bucket)
        => Path.Combine(_directory, $"{scope.ToLowerInvariant()}.{bucket.ToString("D5", CultureInfo.InvariantCulture)}.rows");

    public ValueTask DisposeAsync()
    {
        foreach (var buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still open by a cancelled enumeration; the OS temp directory is cleaned up on its own schedule.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: never let temp-file housekeeping fail a run.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>The binary framing of one keyed row: key, column count, then name and tagged scalar per column.</summary>
internal static class RowCodec
{
    private const byte Null = 0;
    private const byte Text = 1;
    private const byte Boolean = 2;
    private const byte Integer = 3;
    private const byte Real = 4;
    private const byte Instant = 5;
    private const byte Uuid = 6;
    private const byte Exact = 7;

    public static void Write(Stream stream, Guid key, SourceRow row)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Span<byte> keyBytes = stackalloc byte[16];
        key.TryWriteBytes(keyBytes);
        writer.Write(keyBytes);
        var columns = row.Columns.ToList();
        Span<byte> guidBytes = stackalloc byte[16];
        writer.Write7BitEncodedInt(columns.Count);
        foreach (var column in columns)
        {
            writer.Write(column);
            switch (row.Get(column))
            {
                case null:
                    writer.Write(Null);
                    break;
                case string s:
                    writer.Write(Text);
                    writer.Write(s);
                    break;
                case bool b:
                    writer.Write(Boolean);
                    writer.Write(b);
                    break;
                case long l:
                    writer.Write(Integer);
                    writer.Write(l);
                    break;
                case double d:
                    writer.Write(Real);
                    writer.Write(d);
                    break;
                case decimal m:
                    writer.Write(Exact);
                    writer.Write(m);
                    break;
                case DateTimeOffset dto:
                    writer.Write(Instant);
                    writer.Write(dto.UtcTicks);
                    break;
                case Guid g:
                    writer.Write(Uuid);
                    g.TryWriteBytes(guidBytes);
                    writer.Write(guidBytes);
                    break;
                default:
                    writer.Write(Text);
                    writer.Write(SourceRow.Stringify(row.Get(column)) ?? string.Empty);
                    break;
            }
        }
    }

    public static (Guid Key, SourceRow Row) Read(BinaryReader reader)
    {
        var key = new Guid(reader.ReadBytes(16));
        var count = reader.Read7BitEncodedInt();
        var values = new Dictionary<string, object?>(count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            values[name] = reader.ReadByte() switch
            {
                Null => null,
                Text => reader.ReadString(),
                Boolean => reader.ReadBoolean(),
                Integer => reader.ReadInt64(),
                Real => reader.ReadDouble(),
                Instant => new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero),
                Uuid => new Guid(reader.ReadBytes(16)),
                Exact => reader.ReadDecimal(),
                var tag => throw new DeliveryException($"Corrupt spill file: unknown value tag {tag}."),
            };
        }

        return (key, new SourceRow(values));
    }
}
