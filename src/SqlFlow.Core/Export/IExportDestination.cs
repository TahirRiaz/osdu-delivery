using SqlFlow.Core;

namespace SqlFlow.Core.Export;

/// <summary>
/// Where an export writes its files. A dedicated write seam (the file readers' IFileStore is read-only): a
/// destination advertises the locations it handles and opens a writable stream. The local filesystem
/// destination ships now; an Azure Data Lake destination drops in later by implementing this interface, with no
/// runner change. The caller owns the returned stream's lifetime.
/// </summary>
public interface IExportDestination
{
    /// <summary>True if this destination handles the location (a plain/UNC path, or a cloud URI scheme).</summary>
    bool CanHandle(string location);

    /// <summary>Creates (or truncates) a writable stream at the location, creating parent folders as needed.</summary>
    Task<Stream> OpenWriteAsync(string location, CancellationToken ct = default);

    /// <summary>Removes the file at the location if it exists (used to discard an empty-result file).</summary>
    Task DeleteIfExistsAsync(string location, CancellationToken ct = default);

    /// <summary>The size in bytes of the file at the location (0 if absent), read after the write completes.</summary>
    Task<long> GetSizeAsync(string location, CancellationToken ct = default);

    /// <summary>Compress the file at the location in place, replacing it with a single-entry <c>.zip</c> and
    /// returning the new location (legacy ZipTrg). The default is unsupported: a destination that cannot
    /// compress surfaces a clear error rather than silently ignoring a requested zip, so no flow believes it
    /// produced a compressed file when it did not.</summary>
    Task<string> ZipAsync(string location, CancellationToken ct = default)
        => throw new SqlFlowException($"This export destination does not support zipTrg (compression) for '{location}'.");
}
