using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using SqlFlow.Core;
using SqlFlow.Core.Copy;

namespace SqlFlow.Copy;

/// <summary>
/// The local (and UNC) filesystem endpoint. A location is handled here when it is neither an Azure storage URI nor an
/// <c>sftp://</c> URL: a plain path, a <c>file://</c> URI, or a Windows/UNC path. Listing honors the glob, the
/// recursion flag, and the engine-resolved modified window; writes create the target directory tree and, when
/// overwrite is off, fail rather than clobber an existing file. The engine skips an unchanged file by comparing
/// content hashes (<see cref="TargetContentHashAsync"/>) before it transfers anything, so an unchanged file's
/// last-write time is not bumped.
/// </summary>
public sealed class LocalCopyEndpoint : ICopyEndpoint
{
    public bool CanHandle(string location)
        => !AzureBlobCopyEndpoint.IsAzure(location)
           && !location.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<CopyItem> ListAsync(
        CopyEndpoint endpoint, CopyModifiedWindow window, [EnumeratorCancellation] CancellationToken ct)
    {
        var root = Path.GetFullPath(LocalPath(endpoint.Location));
        if (!Directory.Exists(root))
        {
            // A single-file root is a valid source: yield just that file when it matches.
            if (File.Exists(root))
            {
                var info = new FileInfo(root);
                var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (Matches(endpoint, info.Name, modified, window))
                {
                    yield return new CopyItem(root, info.Name, info.Name, modified, info.Length);
                }

                yield break;
            }

            throw new SqlFlowException($"Copy source path not found: '{root}'.");
        }

        var option = endpoint.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(root, "*", option))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (!Matches(endpoint, info.Name, modified, window))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            yield return new CopyItem(file, relative, info.Name, modified, info.Length);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct)
        => await File.ReadAllBytesAsync(absolutePath, ct).ConfigureAwait(false);

    public Task<IReadOnlyDictionary<string, byte[]?>> TargetHashIndexAsync(CopyEndpoint endpoint, CancellationToken ct)
    {
        var root = Path.GetFullPath(LocalPath(endpoint.Location));
        var index = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                // Presence only: local disk stores no hash, so the engine hashes on demand (TargetContentHashAsync)
                // for the files a source matches, not the whole target tree.
                index[relative] = null;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, byte[]?>>(index);
    }

    public async Task<byte[]?> TargetContentHashAsync(CopyEndpoint endpoint, string relativePath, CancellationToken ct)
    {
        var destination = TargetPath(endpoint, relativePath);
        // Local disk stores no content hash, so hash the existing file (MD5, to match what Azure records for a blob,
        // so a local-to-lake or lake-to-local copy compares like-for-like).
        return await ContentHash.OfFileAsync(destination, ct).ConfigureAwait(false);
    }

    public async Task<string> WriteAsync(
        CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, byte[] contentHash, CancellationToken ct)
    {
        var destination = TargetPath(endpoint, relativePath);
        if (!overwrite && File.Exists(destination))
        {
            throw new SqlFlowException($"Copy target '{destination}' already exists and overwrite is disabled.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, content, ct).ConfigureAwait(false);
        return destination;
    }

    /// <summary>Resolves the absolute target path under the endpoint root, refusing a relative path that escapes it.</summary>
    private static string TargetPath(CopyEndpoint endpoint, string relativePath)
    {
        var root = Path.GetFullPath(LocalPath(endpoint.Location));
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(destination, root, StringComparison.Ordinal))
        {
            throw new SqlFlowException($"Copy target path '{relativePath}' escapes the target root '{root}'.");
        }

        return destination;
    }

    private static bool Matches(CopyEndpoint endpoint, string name, DateTimeOffset lastWrite, CopyModifiedWindow window)
        => FileSystemName.MatchesSimpleExpression(endpoint.Pattern, name) && window.Includes(lastWrite);

    private static string LocalPath(string location)
        => location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(location).LocalPath : location;
}
