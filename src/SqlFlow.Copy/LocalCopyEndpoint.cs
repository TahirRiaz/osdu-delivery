using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using SqlFlow.Core;
using SqlFlow.Core.Copy;

namespace SqlFlow.Copy;

/// <summary>
/// The local (and UNC) filesystem endpoint. A location is handled here when it is neither an Azure storage URI nor an
/// <c>sftp://</c> URL: a plain path, a <c>file://</c> URI, or a Windows/UNC path. Listing honors the glob, the
/// recursion flag, and the modified-within window; writes create the target directory tree and, when overwrite is
/// off, fail rather than clobber an existing file.
/// </summary>
public sealed class LocalCopyEndpoint : ICopyEndpoint
{
    private readonly TimeProvider _time;

    public LocalCopyEndpoint(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public bool CanHandle(string location)
        => !AzureBlobCopyEndpoint.IsAzure(location)
           && !SftpCopyEndpoint.IsSftp(location);

    public async IAsyncEnumerable<CopyItem> ListAsync(CopyEndpoint endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        var root = Path.GetFullPath(LocalPath(endpoint.Location));
        if (!Directory.Exists(root))
        {
            // A single-file root is a valid source: yield just that file when it matches.
            if (File.Exists(root))
            {
                var info = new FileInfo(root);
                if (Matches(endpoint, info.Name, info.LastWriteTimeUtc))
                {
                    yield return new CopyItem(root, info.Name, info.Name, info.LastWriteTimeUtc, info.Length);
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
            if (!Matches(endpoint, info.Name, info.LastWriteTimeUtc))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            yield return new CopyItem(file, relative, info.Name, info.LastWriteTimeUtc, info.Length);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct)
        => await File.ReadAllBytesAsync(absolutePath, ct).ConfigureAwait(false);

    public async Task<string> WriteAsync(
        CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct)
    {
        var root = Path.GetFullPath(LocalPath(endpoint.Location));
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        // Never let a crafted relative path escape the target root.
        if (!destination.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(destination, root, StringComparison.Ordinal))
        {
            throw new SqlFlowException($"Copy target path '{relativePath}' escapes the target root '{root}'.");
        }

        if (!overwrite && File.Exists(destination))
        {
            throw new SqlFlowException($"Copy target '{destination}' already exists and overwrite is disabled.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, content, ct).ConfigureAwait(false);
        return destination;
    }

    private bool Matches(CopyEndpoint endpoint, string name, DateTime lastWriteUtc)
    {
        if (!FileSystemName.MatchesSimpleExpression(endpoint.Pattern, name))
        {
            return false;
        }

        if (endpoint.ModifiedWithinDays > 0)
        {
            var cutoff = _time.GetUtcNow().AddDays(-endpoint.ModifiedWithinDays);
            if (new DateTimeOffset(lastWriteUtc, TimeSpan.Zero) < cutoff)
            {
                return false;
            }
        }

        return true;
    }

    private static string LocalPath(string location)
        => location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(location).LocalPath : location;
}
