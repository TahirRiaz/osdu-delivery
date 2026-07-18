using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Renci.SshNet;
using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.Sftp;

namespace SqlFlow.Sftp;

/// <summary>
/// Runs an SFTP transfer flow: connects to the server (password or private key, resolved from secret references),
/// and either downloads the matched remote files into the lake/local target or uploads the matched lake/local files
/// to the server. The lake side handles a data-lake URI (managed-identity authenticated) or a local path. Fully
/// self-contained - it owns its SSH and storage I/O - so the SFTP flow type stands alone. It never throws for a
/// transfer failure; a failed/partial <see cref="SftpRunResult"/> is returned.
/// </summary>
public sealed class SftpEngine
{
    private readonly ISecretResolver _secrets;
    private readonly IAzureCredentialFactory _azure;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Lazy<BlobContainerClient>> _containers = new(StringComparer.Ordinal);

    public SftpEngine(ISecretResolver secrets, IAzureCredentialFactory azure, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(azure);
        ArgumentNullException.ThrowIfNull(time);
        _secrets = secrets;
        _azure = azure;
        _time = time;
    }

    public async Task<SftpRunResult> RunAsync(SftpFlow flow, Guid runId, IRunEventSink log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(log);
        var sw = Stopwatch.StartNew();
        var files = new List<SftpFileResult>();
        var matched = 0;
        var skipped = 0;

        try
        {
            // One connection serves every step: a flow that moves several file sets does so over a single session.
            using var client = await ConnectAsync(flow.Server, ct).ConfigureAwait(false);
            foreach (var step in flow.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var cutoff = step.ModifiedWithinDays > 0 ? _time.GetUtcNow().AddDays(-step.ModifiedWithinDays) : (DateTimeOffset?)null;

                if (flow.Direction == SftpDirection.Download)
                {
                    var remote = ListRemote(client, step, cutoff);
                    matched += remote.Count;
                    log.Log(RunLogLevel.Info, "sftp.list", $"matched {remote.Count} remote file(s) under '{step.RemotePath}'.");
                    foreach (var (full, relative, _) in remote)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var buffer = new MemoryStream();
                        client.DownloadFile(full, buffer);
                        var rel = flow.PreserveStructure ? relative : relative[(relative.LastIndexOf('/') + 1)..];
                        var (location, wrote, hash) = await WriteLakeAsync(
                            step.Local, rel, buffer.ToArray(), flow.Overwrite, flow.SkipUnchanged, ct).ConfigureAwait(false);
                        if (wrote)
                        {
                            files.Add(new SftpFileResult(location, buffer.Length, hash));
                            log.Log(RunLogLevel.Info, "sftp.download", $"downloaded {buffer.Length} byte(s) -> '{location}'.");
                        }
                        else
                        {
                            skipped++;
                            log.Log(RunLogLevel.Info, "sftp.skip", $"skipped '{location}' (no change).");
                        }
                    }
                }
                else
                {
                    var local = await ListLakeAsync(step.Local, step.Pattern, step.Recursive, cutoff, ct).ConfigureAwait(false);
                    matched += local.Count;
                    log.Log(RunLogLevel.Info, "sftp.list", $"matched {local.Count} local file(s) at '{step.Local}'.");
                    foreach (var (absolute, relative, _) in local)
                    {
                        ct.ThrowIfCancellationRequested();
                        var bytes = await ReadLakeAsync(step.Local, absolute, ct).ConfigureAwait(false);
                        var rel = flow.PreserveStructure ? relative : relative[(relative.LastIndexOf('/') + 1)..];
                        var remote = $"{step.RemotePath.TrimEnd('/')}/{rel.TrimStart('/')}";
                        if (!flow.Overwrite && client.Exists(remote))
                        {
                            throw new SqlFlowException($"SFTP target '{remote}' already exists and overwrite is disabled.");
                        }

                        EnsureRemoteDirectory(client, remote[..remote.LastIndexOf('/')]);
                        using var stream = new MemoryStream(bytes, writable: false);
                        client.UploadFile(stream, remote, canOverride: flow.Overwrite);
                        files.Add(new SftpFileResult($"sftp://{flow.Server.Host}:{flow.Server.Port}{remote}", bytes.Length));
                        log.Log(RunLogLevel.Info, "sftp.upload", $"uploaded {bytes.Length} byte(s) -> '{remote}'.");
                    }
                }
            }

            sw.Stop();
            return new SftpRunResult
            {
                RunId = runId, Success = true, DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3),
                Matched = matched, FilesTransferred = files.Count, FilesSkipped = skipped,
                BytesTransferred = files.Sum(f => f.SizeBytes), Files = files,
            };
        }
        // A cancellation is deliberately NOT caught here: cancelled is a distinct terminal state from failed, and it
        // is the caller that knows which one this is (an operator cancel of a queued run vs. a node shutdown). Turning
        // it into a failed result here would record an operator's cancel as a failure and strand a shutdown-interrupted
        // run that should be requeued, so it propagates to RunWorker / the CLI, which own that decision.
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is SqlFlowException or IOException or InvalidOperationException or RequestFailedException)
        {
            sw.Stop();
            var message = SecretHygiene.RedactedMessage(ex.Message);
            log.Log(RunLogLevel.Info, "sftp.error", message);
            return new SftpRunResult
            {
                RunId = runId, Success = false, Error = message, DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3),
                Matched = matched, FilesTransferred = files.Count, FilesSkipped = skipped,
                BytesTransferred = files.Sum(f => f.SizeBytes), Files = files,
            };
        }
    }

    // ---- SFTP side ----------------------------------------------------------------------------------------------

    private List<(string Full, string Relative, long Size)> ListRemote(SftpClient client, SftpStep step, DateTimeOffset? cutoff)
    {
        var root = step.RemotePath;
        var found = new List<(string, string, long)>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var entry in client.ListDirectory(stack.Pop()))
            {
                if (entry.Name is "." or "..")
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    if (step.Recursive)
                    {
                        stack.Push(entry.FullName);
                    }

                    continue;
                }

                if (!entry.IsRegularFile || !FileSystemName.MatchesSimpleExpression(step.Pattern, entry.Name))
                {
                    continue;
                }

                if (cutoff is { } c && new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero) < c)
                {
                    continue;
                }

                var relative = entry.FullName.StartsWith(root, StringComparison.Ordinal)
                    ? entry.FullName[root.Length..].TrimStart('/')
                    : entry.Name;
                found.Add((entry.FullName, relative, entry.Length));
            }
        }

        return found;
    }

    private async Task<SftpClient> ConnectAsync(SftpServer server, CancellationToken ct)
    {
        SftpClient client;
        if (!string.IsNullOrWhiteSpace(server.PrivateKeyRef))
        {
            var pem = await _secrets.ResolveAsync(server.PrivateKeyRef!, ct).ConfigureAwait(false);
            var passphrase = string.IsNullOrWhiteSpace(server.PassphraseRef)
                ? null
                : await _secrets.ResolveAsync(server.PassphraseRef!, ct).ConfigureAwait(false);
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(pem));
            var keyFile = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, passphrase);
            client = new SftpClient(new ConnectionInfo(server.Host, server.Port, server.Username, new PrivateKeyAuthenticationMethod(server.Username, keyFile)));
        }
        else if (!string.IsNullOrWhiteSpace(server.PasswordRef))
        {
            var password = await _secrets.ResolveAsync(server.PasswordRef!, ct).ConfigureAwait(false);
            client = new SftpClient(server.Host, server.Port, server.Username, password);
        }
        else
        {
            throw new SqlFlowException("An SFTP flow requires 'server.passwordRef' or 'server.privateKeyRef'.");
        }

        await client.ConnectAsync(ct).ConfigureAwait(false);
        return client;
    }

    private static void EnsureRemoteDirectory(SftpClient client, string dir)
    {
        if (string.IsNullOrEmpty(dir) || client.Exists(dir))
        {
            return;
        }

        var path = string.Empty;
        foreach (var part in dir.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + part;
            if (!client.Exists(path))
            {
                client.CreateDirectory(path);
            }
        }
    }

    // ---- Lake / local side --------------------------------------------------------------------------------------

    /// <summary>Writes a downloaded file to the lake/local target, returning the location, whether bytes were actually
    /// written, and the content hash (lowercase hex MD5). When <paramref name="skipUnchanged"/> is set and the target
    /// already holds byte-identical content the write is skipped, so an unchanged re-download does not bump the
    /// target's last-modified time and re-trigger downstream ingestion.</summary>
    private async Task<(string Location, bool Wrote, string Hash)> WriteLakeAsync(
        string root, string relative, ReadOnlyMemory<byte> content, bool overwrite, bool skipUnchanged, CancellationToken ct)
    {
        var md5 = ContentHash.Md5(content.Span);
        var hex = Convert.ToHexString(md5).ToLowerInvariant();

        if (AzureBlobLocation.IsAzureStorageUri(root))
        {
            var loc = AzureBlobLocation.Parse(root);
            var container = Container(loc);
            var blobName = (loc.BlobPath.TrimEnd('/').Length == 0 ? relative : $"{loc.BlobPath.TrimEnd('/')}/{relative}").TrimStart('/');
            var blob = container.GetBlobClient(blobName);

            if (!overwrite)
            {
                await UploadStampedAsync(blob, content, md5, new BlobRequestConditions { IfNoneMatch = ETag.All }, ct).ConfigureAwait(false);
                return (loc.UriFor(blobName), true, hex);
            }

            if (!skipUnchanged)
            {
                await UploadStampedAsync(blob, content, md5, null, ct).ConfigureAwait(false);
                return (loc.UriFor(blobName), true, hex);
            }

            var uploaded = await ConditionalWrite.WriteIfChangedAsync(
                content,
                token => BlobContentHashAsync(blob, token),
                (hash, token) => UploadStampedAsync(blob, content, hash, null, token),
                ct).ConfigureAwait(false);
            return (loc.UriFor(blobName), uploaded, hex);
        }

        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!overwrite && File.Exists(destination))
        {
            throw new SqlFlowException($"SFTP target '{destination}' already exists and overwrite is disabled.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (overwrite && skipUnchanged)
        {
            var wrote = await ConditionalWrite.WriteIfChangedAsync(
                content,
                token => ContentHash.OfFileAsync(destination, token),
                (_, token) => File.WriteAllBytesAsync(destination, content, token),
                ct).ConfigureAwait(false);
            return (destination, wrote, hex);
        }

        await File.WriteAllBytesAsync(destination, content, ct).ConfigureAwait(false);
        return (destination, true, hex);
    }

    private static async Task UploadStampedAsync(
        BlobClient blob, ReadOnlyMemory<byte> content, byte[] md5, BlobRequestConditions? conditions, CancellationToken ct)
    {
        var options = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentHash = md5 } };
        if (conditions is not null)
        {
            options.Conditions = conditions;
        }

        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await blob.UploadAsync(stream, options, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> BlobContentHashAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            return props.Value.ContentHash;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task<List<(string Absolute, string Relative, long Size)>> ListLakeAsync(
        string root, string pattern, bool recursive, DateTimeOffset? cutoff, CancellationToken ct)
    {
        var found = new List<(string, string, long)>();
        if (AzureBlobLocation.IsAzureStorageUri(root))
        {
            var loc = AzureBlobLocation.Parse(root);
            var container = Container(loc);
            var prefix = loc.BlobPath.Length == 0 ? null : loc.BlobPath.TrimEnd('/') + "/";
            await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct).ConfigureAwait(false))
            {
                var name = blob.Name;
                var leaf = name[(name.LastIndexOf('/') + 1)..];
                var tail = prefix is null ? name : name[prefix.Length..];
                if (leaf.Length == 0 || (!recursive && tail.Contains('/', StringComparison.Ordinal)) || !FileSystemName.MatchesSimpleExpression(pattern, leaf))
                {
                    continue;
                }

                if (cutoff is { } c && blob.Properties.LastModified is { } m && m < c)
                {
                    continue;
                }

                found.Add((name, tail, blob.Properties.ContentLength ?? -1));
            }

            return found;
        }

        var full = Path.GetFullPath(root);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(full, "*", option))
        {
            var info = new FileInfo(file);
            if (!FileSystemName.MatchesSimpleExpression(pattern, info.Name))
            {
                continue;
            }

            if (cutoff is { } c && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) < c)
            {
                continue;
            }

            found.Add((file, Path.GetRelativePath(full, file).Replace('\\', '/'), info.Length));
        }

        return found;
    }

    private async Task<byte[]> ReadLakeAsync(string root, string absolute, CancellationToken ct)
    {
        if (AzureBlobLocation.IsAzureStorageUri(root))
        {
            var loc = AzureBlobLocation.Parse(root);
            var response = await Container(loc).GetBlobClient(absolute).DownloadContentAsync(ct).ConfigureAwait(false);
            return response.Value.Content.ToArray();
        }

        return await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
    }

    private BlobContainerClient Container(AzureBlobLocation loc)
        => _containers.GetOrAdd(
            $"{loc.BlobServiceEndpoint}|{loc.Container}",
            _ => new Lazy<BlobContainerClient>(() => new BlobServiceClient(loc.BlobServiceEndpoint, _azure.Create()).GetBlobContainerClient(loc.Container))).Value;
}
