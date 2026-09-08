using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Drops;

/// <summary>A resolved drop: its root location and manifest.</summary>
public sealed record Drop(string Location, DropManifest Manifest)
{
    /// <summary>Joins a drop-relative path onto the location, for both URI and file-system locations.</summary>
    public string Resolve(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (Location.Contains("://", StringComparison.Ordinal))
        {
            return Location.TrimEnd('/') + "/" + rel;
        }

        return Path.Combine(Location, rel.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>One payload chunk file, in delivery order.</summary>
public sealed record PayloadChunk(int Index, string Path, long Size);

/// <summary>
/// Reads a drop: the manifest, the source-shaped records (parsed, one row group at a time), and the opaque payload
/// chunks (never parsed: opened as streams and copied, design.md section 13.1).
/// </summary>
public interface IDropReader
{
    Task<Drop> OpenAsync(string location, string manifestName, CancellationToken ct = default);

    /// <summary>
    /// Streams every record with its child-scope rows attached. Child scopes are read fully and indexed by parent
    /// key before the root scope streams, because a record needs all its children to render.
    /// </summary>
    IAsyncEnumerable<SourceRecord> ReadRecordsAsync(Drop drop, CancellationToken ct = default);

    /// <summary>Lists the chunks of one record's payload, ordered by chunk name.</summary>
    Task<IReadOnlyList<PayloadChunk>> ListPayloadChunksAsync(Drop drop, string payloadName, Guid deliveryKey, CancellationToken ct = default);

    /// <summary>Lists chunks under an already-resolved payload location (for retries from the ledger).</summary>
    Task<IReadOnlyList<PayloadChunk>> ListPayloadChunksAsync(string payloadLocation, CancellationToken ct = default);

    Task<Stream> OpenChunkAsync(PayloadChunk chunk, CancellationToken ct = default);

    /// <summary>The location of a record's payload folder inside the drop, recorded in the ledger for retries.</summary>
    string PayloadLocation(Drop drop, string payloadName, Guid deliveryKey);
}
