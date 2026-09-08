using Azure;
using Azure.Storage.Blobs;
using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Storage;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// Writes a file (create or overwrite) and answers whether a location exists, for the locations one file store
/// family handles. The platform's <see cref="IFileStore"/> is read-only by design (the engine only ever reads
/// sources); snapshots and the known-state publication are the two things the delivery domain writes, so the
/// write side lives here, next to the reads it pairs with.
/// </summary>
public interface IFileWriter
{
    bool CanHandle(string location);

    Task WriteAsync(string location, Stream content, CancellationToken ct = default);

    Task<bool> ExistsAsync(string location, CancellationToken ct = default);
}

/// <summary>
/// Picks the store (and the writer) for a location: the platform's local and Azure blob stores for reads, the
/// matching writers for the few writes. One registry per host, shared by the drop reader, the snapshot store and
/// the known-state publisher.
/// </summary>
public sealed class FileStoreRegistry
{
    private readonly IReadOnlyList<IFileStore> _stores;
    private readonly IReadOnlyList<IFileWriter> _writers;

    public FileStoreRegistry(IEnumerable<IFileStore> stores, IEnumerable<IFileWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(writers);
        _stores = stores.ToList();
        _writers = writers.ToList();
    }

    public IFileStore For(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        return _stores.FirstOrDefault(s => s.CanHandle(location))
            ?? throw new DeliveryException($"No file store handles location '{location}'. Use a local path, or an abfss:// or https://<account>.blob.core.windows.net URI.");
    }

    public Task WriteAsync(string location, Stream content, CancellationToken ct = default)
        => Writer(location).WriteAsync(location, content, ct);

    public Task<bool> ExistsAsync(string location, CancellationToken ct = default)
        => Writer(location).ExistsAsync(location, ct);

    private IFileWriter Writer(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        return _writers.FirstOrDefault(w => w.CanHandle(location))
            ?? throw new DeliveryException($"No file writer handles location '{location}'. Snapshots and known-state publications go to a local path or an Azure Storage URI.");
    }
}

/// <summary>Writes to the local and UNC file system, atomically (write to a sibling temp file, then move into place).</summary>
public sealed class LocalFileWriter : IFileWriter
{
    private readonly LocalFileStore _store = new();

    public bool CanHandle(string location) => _store.CanHandle(location);

    public async Task WriteAsync(string location, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var directory = Path.GetDirectoryName(location);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = location + ".tmp";
        await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await content.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        File.Move(temp, location, overwrite: true);
    }

    public Task<bool> ExistsAsync(string location, CancellationToken ct = default)
        => Task.FromResult(File.Exists(location) || Directory.Exists(location));
}

/// <summary>
/// Writes blobs under the same credential the platform's <see cref="AzureBlobFileStore"/> reads with, so a
/// snapshot store or a known-state location on the lake needs no second identity.
/// </summary>
public sealed class AzureBlobFileWriter : IFileWriter
{
    private readonly IAzureCredentialFactory _credentials;

    public AzureBlobFileWriter(IAzureCredentialFactory credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task WriteAsync(string location, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var blob = Blob(location);
        try
        {
            await blob.UploadAsync(content, overwrite: true, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new DeliveryException($"Could not write '{location}' (status {ex.Status}): {ex.Message}", ex);
        }
    }

    public async Task<bool> ExistsAsync(string location, CancellationToken ct = default)
    {
        try
        {
            return await Blob(location).ExistsAsync(ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new DeliveryException($"Could not check '{location}' (status {ex.Status}): {ex.Message}", ex);
        }
    }

    private BlobClient Blob(string location)
    {
        var parsed = AzureBlobLocation.Parse(location);
        var service = new BlobServiceClient(parsed.BlobServiceEndpoint, _credentials.Create());
        return service.GetBlobContainerClient(parsed.Container).GetBlobClient(parsed.BlobPath);
    }
}
