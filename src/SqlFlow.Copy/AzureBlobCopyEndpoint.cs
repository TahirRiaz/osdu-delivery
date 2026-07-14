using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Copy;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Copy;

/// <summary>
/// The Azure Blob / ADLS Gen2 endpoint, for both the ADLS authority form (<c>abfss://fs@account.dfs…/path</c>) and
/// the https URL form. It authenticates with the ambient managed identity / az login by default (the same credential
/// Key Vault, invoke, and the acquisition landing store use, so no per-flow storage secret is needed), or with a
/// connection string / SAS token / account key when the endpoint declares a secret reference (a vendor drop zone that
/// only issues a key). One container client per resolved endpoint is built once and reused across the run's reads
/// and writes so a secret is fetched at most once.
/// </summary>
public sealed class AzureBlobCopyEndpoint : ICopyEndpoint
{
    private readonly IAzureCredentialFactory _credentials;
    private readonly ISecretResolver _secrets;
    private readonly ConcurrentDictionary<string, Lazy<Task<BlobContainerClient>>> _clients = new(StringComparer.Ordinal);

    public AzureBlobCopyEndpoint(IAzureCredentialFactory credentials, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(secrets);
        _credentials = credentials;
        _secrets = secrets;
    }

    public static bool IsAzure(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public bool CanHandle(string location) => IsAzure(location);

    public async IAsyncEnumerable<CopyItem> ListAsync(CopyEndpoint endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        var prefix = loc.BlobPath.Length == 0 ? null : loc.BlobPath.TrimEnd('/') + "/";
        var cutoff = endpoint.ModifiedWithinDays > 0 ? DateTimeOffset.UtcNow.AddDays(-endpoint.ModifiedWithinDays) : (DateTimeOffset?)null;

        await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var name = blob.Name;
            var leaf = name[(name.LastIndexOf('/') + 1)..];
            if (leaf.Length == 0)
            {
                continue; // a virtual directory marker
            }

            // 'recursive: false' keeps only blobs directly under the prefix (no further '/').
            if (!endpoint.Recursive)
            {
                var tail = prefix is null ? name : name[prefix.Length..];
                if (tail.Contains('/', StringComparison.Ordinal))
                {
                    continue;
                }
            }

            if (!FileSystemName.MatchesSimpleExpression(endpoint.Pattern, leaf))
            {
                continue;
            }

            var modified = blob.Properties.LastModified;
            if (cutoff is { } c && modified is { } m && m < c)
            {
                continue;
            }

            var relative = prefix is null ? name : name[prefix.Length..];
            yield return new CopyItem(name, relative, leaf, modified, blob.Properties.ContentLength ?? -1);
        }
    }

    public async Task<byte[]> ReadAsync(CopyEndpoint endpoint, string absolutePath, CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        try
        {
            var response = await container.GetBlobClient(absolutePath).DownloadContentAsync(ct).ConfigureAwait(false);
            return response.Value.Content.ToArray();
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    public async Task<string> WriteAsync(
        CopyEndpoint endpoint, string relativePath, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct)
    {
        var loc = AzureBlobLocation.Parse(endpoint.Location);
        var container = await ContainerAsync(endpoint, loc, ct).ConfigureAwait(false);
        var basePath = loc.BlobPath.TrimEnd('/');
        var blobName = (basePath.Length == 0 ? relativePath : $"{basePath}/{relativePath}").TrimStart('/');
        var blob = container.GetBlobClient(blobName);
        try
        {
            var options = new BlobUploadOptions();
            if (!overwrite)
            {
                options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            }

            using var stream = new MemoryStream(content.ToArray(), writable: false);
            await blob.UploadAsync(stream, options, ct).ConfigureAwait(false);
            return loc.UriFor(blobName);
        }
        catch (RequestFailedException ex) when (!overwrite && ex.Status == 409)
        {
            throw new SqlFlowException($"Copy target blob '{loc.UriFor(blobName)}' already exists and overwrite is disabled.", ex);
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(endpoint.Location, ex);
        }
    }

    private Task<BlobContainerClient> ContainerAsync(CopyEndpoint endpoint, AzureBlobLocation loc, CancellationToken ct)
        => _clients.GetOrAdd(
            $"{loc.BlobServiceEndpoint}|{loc.Container}",
            _ => new Lazy<Task<BlobContainerClient>>(() => BuildContainerAsync(endpoint, loc, ct))).Value;

    private async Task<BlobContainerClient> BuildContainerAsync(CopyEndpoint endpoint, AzureBlobLocation loc, CancellationToken ct)
    {
        BlobServiceClient service;
        if (!string.IsNullOrWhiteSpace(endpoint.ConnectionStringRef))
        {
            service = new BlobServiceClient(await _secrets.ResolveAsync(endpoint.ConnectionStringRef!, ct).ConfigureAwait(false));
        }
        else if (!string.IsNullOrWhiteSpace(endpoint.AccountKeyRef))
        {
            var key = await _secrets.ResolveAsync(endpoint.AccountKeyRef!, ct).ConfigureAwait(false);
            service = new BlobServiceClient(
                $"DefaultEndpointsProtocol=https;AccountName={loc.Account};AccountKey={key};EndpointSuffix=core.windows.net");
        }
        else if (!string.IsNullOrWhiteSpace(endpoint.SasTokenRef))
        {
            var sas = (await _secrets.ResolveAsync(endpoint.SasTokenRef!, ct).ConfigureAwait(false)).TrimStart('?');
            service = new BlobServiceClient(new Uri($"{loc.BlobServiceEndpoint}?{sas}"));
        }
        else
        {
            service = new BlobServiceClient(loc.BlobServiceEndpoint, _credentials.Create());
        }

        return service.GetBlobContainerClient(loc.Container);
    }

    private static SqlFlowException Translate(string location, Exception ex)
    {
        var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        return cause switch
        {
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed for copy endpoint '{location}' (status {rfe.Status}): {rfe.Message}", ex),
            _ => new SqlFlowException($"Could not access Azure Storage copy endpoint '{location}': {cause.Message}", ex),
        };
    }
}
