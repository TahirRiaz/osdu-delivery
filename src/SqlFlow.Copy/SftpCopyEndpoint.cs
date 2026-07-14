using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using Renci.SshNet;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Copy;

/// <summary>
/// The SFTP endpoint: an <c>sftp://host[:port]/rootPath</c> location, authenticated with a password or a private key
/// (both resolved from secret references). As a source it lists the remote tree (honoring the glob, recursion, and
/// modified-within window) and reads each file's bytes; as a target it uploads to <c>rootPath/relative</c>, creating
/// remote directories as needed. Each transfer opens its own connection, so a run holds no long-lived session.
/// </summary>
public sealed class SftpCopyEndpoint : ICopyEndpoint
{
    private readonly TimeProvider _time;
    private readonly ISecretResolver _secrets;

    public SftpCopyEndpoint(TimeProvider time, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(secrets);
        _time = time;
        _secrets = secrets;
    }

    public static bool IsSftp(string location) => location.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase);

    public bool CanHandle(string location) => IsSftp(location);

    public async IAsyncEnumerable<CopyItem> ListAsync(CopyEndpoint endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        var (host, port, root) = ParseUrl(endpoint.Location);
        var cutoff = endpoint.ModifiedWithinDays > 0 ? _time.GetUtcNow().AddDays(-endpoint.ModifiedWithinDays) : (DateTimeOffset?)null;

        using var client = await ConnectAsync(endpoint, host, port, ct).ConfigureAwait(false);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var entry in client.ListDirectory(dir))
            {
                if (entry.Name is "." or "..")
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    if (endpoint.Recursive)
                    {
                        stack.Push(entry.FullName);
                    }

                    continue;
                }

                if (!entry.IsRegularFile || !FileSystemName.MatchesSimpleExpression(endpoint.Pattern, entry.Name))
                {
                    continue;
                }

                var modified = new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero);
                if (cutoff is { } c && modified < c)
                {
                    continue;
                }

                var relative = entry.FullName.StartsWith(root, StringComparison.Ordinal)
                    ? entry.FullName[root.Length..].TrimStart('/')
                    : entry.Name;
                yield return new CopyItem(entry.FullName, relative, entry.Name, modified, entry.Length);
            }
        }
    }

    public async Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct)
    {
        var (host, port, _) = ParseUrl(endpoint.Location);
        using var client = await ConnectAsync(endpoint, host, port, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        client.DownloadFile(absolutePath, buffer);
        return buffer.ToArray();
    }

    public async Task<string> WriteAsync(
        CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct)
    {
        var (host, port, root) = ParseUrl(endpoint.Location);
        var remote = $"{root.TrimEnd('/')}/{relativePath.TrimStart('/')}";
        using var client = await ConnectAsync(endpoint, host, port, ct).ConfigureAwait(false);
        if (!overwrite && client.Exists(remote))
        {
            throw new SqlFlowException($"Copy target 'sftp://{host}{remote}' already exists and overwrite is disabled.");
        }

        EnsureRemoteDirectory(client, remote[..remote.LastIndexOf('/')]);
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        client.UploadFile(stream, remote, canOverride: overwrite);
        return $"sftp://{host}:{port}{remote}";
    }

    private static void EnsureRemoteDirectory(SftpClient client, string dir)
    {
        if (string.IsNullOrEmpty(dir) || client.Exists(dir))
        {
            return;
        }

        var parts = dir.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var part in parts)
        {
            path += "/" + part;
            if (!client.Exists(path))
            {
                client.CreateDirectory(path);
            }
        }
    }

    private async Task<SftpClient> ConnectAsync(CopyEndpoint endpoint, string host, int port, CancellationToken ct)
    {
        var username = endpoint.Username
            ?? throw new SqlFlowException("An SFTP copy endpoint requires 'username'.");

        SftpClient client;
        if (!string.IsNullOrWhiteSpace(endpoint.PrivateKeyRef))
        {
            var pem = await _secrets.ResolveAsync(endpoint.PrivateKeyRef!, ct).ConfigureAwait(false);
            var passphrase = string.IsNullOrWhiteSpace(endpoint.PassphraseRef)
                ? null
                : await _secrets.ResolveAsync(endpoint.PassphraseRef!, ct).ConfigureAwait(false);
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(pem));
            var keyFile = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, passphrase);
            client = new SftpClient(new ConnectionInfo(host, port, username, new PrivateKeyAuthenticationMethod(username, keyFile)));
        }
        else if (!string.IsNullOrWhiteSpace(endpoint.PasswordRef))
        {
            var password = await _secrets.ResolveAsync(endpoint.PasswordRef!, ct).ConfigureAwait(false);
            client = new SftpClient(host, port, username, password);
        }
        else
        {
            throw new SqlFlowException("An SFTP copy endpoint requires 'passwordRef' or 'privateKeyRef'.");
        }

        await client.ConnectAsync(ct).ConfigureAwait(false);
        return client;
    }

    private static (string Host, int Port, string Root) ParseUrl(string location)
    {
        var uri = new Uri(location, UriKind.Absolute);
        var root = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        return (uri.Host, uri.Port > 0 ? uri.Port : 22, root);
    }
}
