using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SqlFlow.Core.Model;

namespace SqlFlow.Delivery.Storage;

/// <summary>One rendered record waiting in a work batch: its key, its target id and its canonical document.</summary>
public sealed record WorkItem(Guid Key, string TargetId, string Document);

/// <summary>Where one document sits inside a work batch: the batch index and the byte range of its line.</summary>
public readonly record struct DocumentRef(int Batch, long Offset, int Length)
{
    /// <summary>The compact text the ledger stores: batch:offset:length.</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Batch}:{Offset}:{Length}");

    public static DocumentRef Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var parts = text.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var batch)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            throw new DeliveryException($"'{text}' is not a document reference (batch:offset:length).");
        }

        return new DocumentRef(batch, offset, length);
    }

    public static bool TryParse(string? text, out DocumentRef value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            value = Parse(text);
            return true;
        }
        catch (DeliveryException)
        {
            return false;
        }
    }
}

/// <summary>
/// The work batches of a submission (design.md section 16.2): JSON-lines files of rendered documents the intake
/// writes to the flow's work location and the drains read back, so the ledger holds a reference per record
/// instead of the document itself. A batch is one file, one claim, one unit of progress. Lines are written as
/// they render and read as they are needed: a batch of any size never sits in memory.
/// </summary>
public static class WorkBatchFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string DirectoryFor(string workRoot, Guid submissionId)
        => Join(workRoot, submissionId.ToString("D"));

    public static string PathFor(string workRoot, Guid submissionId, int batch)
        => Join(DirectoryFor(workRoot, submissionId), "batch-" + batch.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary>The bytes of one line (without the newline): key, target id and the document, as one JSON object.</summary>
    public static byte[] Encode(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var builder = new StringBuilder(item.Document.Length + 128);
        builder.Append("{\"key\":\"").Append(item.Key.ToString("D")).Append("\",\"targetId\":");
        builder.Append(JsonSerializer.Serialize(item.TargetId));
        builder.Append(",\"document\":").Append(item.Document).Append('}');
        return Utf8.GetBytes(builder.ToString());
    }

    public static WorkItem Decode(ReadOnlySpan<byte> line)
    {
        using var document = JsonDocument.Parse(line.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("key", out var key) || !Guid.TryParse(key.GetString(), out var parsedKey)
            || !root.TryGetProperty("targetId", out var target) || target.GetString() is not { } targetId
            || !root.TryGetProperty("document", out var doc) || doc.ValueKind != JsonValueKind.Object)
        {
            throw new DeliveryException("A work batch line is not a work item (key, targetId, document).");
        }

        return new WorkItem(parsedKey, targetId, doc.GetRawText());
    }

    /// <summary>Reads one document by its reference: a range read of the batch file.</summary>
    public static async Task<WorkItem> ReadOneAsync(FileStoreRegistry stores, string workRoot, Guid submissionId, DocumentRef reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var path = PathFor(workRoot, submissionId, reference.Batch);
        await using var stream = await stores.OpenRangeAsync(path, reference.Offset, reference.Length, ct).ConfigureAwait(false);
        var buffer = new byte[reference.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new DeliveryException($"{path}: the batch file ended before document {reference}; the work location was pruned or rewritten.");
            }

            read += n;
        }

        return Decode(buffer);
    }

    /// <summary>Streams every item of a batch file in order, with each line's reference.</summary>
    public static async IAsyncEnumerable<(WorkItem Item, DocumentRef Reference)> ReadAllAsync(
        FileStoreRegistry stores, string workRoot, Guid submissionId, int batch, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var path = PathFor(workRoot, submissionId, batch);
        await using var stream = await stores.For(path).OpenReadAsync(new FileRef { Path = path, Name = Path.GetFileName(path) }, ct).ConfigureAwait(false);
        var buffer = new byte[1 << 16];
        var line = new MemoryStream();
        long offset = 0;
        long lineStart = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                line.Write(buffer, start, i - start);
                if (line.Length > 0)
                {
                    yield return (Decode(line.GetBuffer().AsSpan(0, (int)line.Length)), new DocumentRef(batch, lineStart, (int)line.Length));
                }

                line.SetLength(0);
                start = i + 1;
                lineStart = offset + start;
            }

            line.Write(buffer, start, read - start);
            offset += read;
        }

        if (line.Length > 0)
        {
            yield return (Decode(line.GetBuffer().AsSpan(0, (int)line.Length)), new DocumentRef(batch, lineStart, (int)line.Length));
        }
    }

    private static string Join(string root, string name)
        => root.Contains("://", StringComparison.Ordinal) ? root.TrimEnd('/') + "/" + name : Path.Combine(root, name);
}

/// <summary>Writes one work batch file, line by line, straight to storage; the batch is complete when disposed.</summary>
public sealed class WorkBatchWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly int _batch;
    private long _offset;

    private WorkBatchWriter(Stream stream, int batch, string path)
    {
        _stream = stream;
        _batch = batch;
        Path = path;
    }

    public string Path { get; }

    public int Batch => _batch;

    public int Count { get; private set; }

    public long Bytes => _offset;

    public static async Task<WorkBatchWriter> OpenAsync(FileStoreRegistry stores, string workRoot, Guid submissionId, int batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var path = WorkBatchFile.PathFor(workRoot, submissionId, batch);
        var stream = await stores.OpenWriteAsync(path, ct).ConfigureAwait(false);
        return new WorkBatchWriter(stream, batch, path);
    }

    /// <summary>Appends one item and returns where it landed.</summary>
    public async Task<DocumentRef> WriteAsync(WorkItem item, CancellationToken ct = default)
    {
        var bytes = WorkBatchFile.Encode(item);
        var reference = new DocumentRef(_batch, _offset, bytes.Length);
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.WriteAsync(new[] { (byte)'\n' }, ct).ConfigureAwait(false);
        _offset += bytes.Length + 1;
        Count++;
        return reference;
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
