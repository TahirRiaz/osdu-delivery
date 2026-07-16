using SqlFlow.Core.Copy;

namespace SqlFlow.Copy;

/// <summary>
/// One end of a copy: it lists and reads files (as a source) and writes them (as a target). The engine selects an
/// endpoint by the <see cref="CopyEndpoint.Location"/> scheme through <see cref="CanHandle"/>, so a copy flow's two
/// sides can be any pair of storage implementations (local disk, Azure Blob / ADLS Gen2) - every direction with one
/// engine. Reads and writes are whole-file byte transfers; the engine owns the zip/unzip transformation between them.
/// </summary>
public interface ICopyEndpoint
{
    /// <summary>Whether this endpoint handles the given location scheme (local path, azure storage URI, sftp URL).</summary>
    bool CanHandle(string location);

    /// <summary>Lists the files under the source endpoint that match its pattern and recursion and fall within the
    /// engine-resolved <paramref name="window"/> (the run's backfill override or the endpoint's declared
    /// <c>modifiedWithinDays</c> default). Each item carries an absolute locator (for <see cref="ReadAsync"/>) and a
    /// path relative to the root.</summary>
    IAsyncEnumerable<CopyItem> ListAsync(CopyEndpoint endpoint, CopyModifiedWindow window, CancellationToken ct);

    /// <summary>Reads a file's bytes by the absolute locator produced in <see cref="ListAsync"/>.</summary>
    Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct);

    /// <summary>Writes bytes to <paramref name="relativePath"/> under the target endpoint's root. Returns the
    /// resolved absolute location written, for the run manifest.</summary>
    Task<string> WriteAsync(CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct);
}

/// <summary>One file discovered at a source endpoint.</summary>
/// <param name="AbsolutePath">The absolute locator the endpoint reads the bytes back with.</param>
/// <param name="RelativePath">The path relative to the source root, used to compose the target path (forward slashes).</param>
/// <param name="Name">The file's leaf name.</param>
/// <param name="Modified">The last-modified timestamp, when the endpoint reports one.</param>
/// <param name="Size">The file size in bytes, when known (-1 otherwise).</param>
public sealed record CopyItem(string AbsolutePath, string RelativePath, string Name, DateTimeOffset? Modified, long Size);
