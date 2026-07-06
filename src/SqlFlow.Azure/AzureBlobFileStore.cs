using System.IO.Enumeration;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;

namespace SqlFlow.Azure;

/// <summary>
/// Azure Blob / ADLS Gen2 file store: reads source files from <c>abfss://</c>, <c>wasbs://</c>, and the
/// <c>https://&lt;account&gt;.blob|dfs.core.windows.net</c> URL forms, so file discovery and loads work against a
/// data lake exactly as they do on local disk. It authenticates through the shared
/// <see cref="IAzureCredentialFactory"/>, which honors <c>SQLFLOW_AZURE_AUTH</c> and by default chains managed
/// identity, a service principal, then Azure CLI (<c>az login</c>) - the same credential Key Vault, invoke, and
/// DuckDB cloud reads use, with no per-flow secret.
///
/// Listing uses hierarchical blob enumeration so a partitioned lake is walked one virtual directory at a time and
/// the discovery filter can prune whole out-of-window partition subtrees (<see cref="IFileDiscoveryFilter"/>)
/// before they are enumerated - the same pushdown the local store performs.
/// </summary>
public sealed class AzureBlobFileStore : IFileStore
{
    private readonly IAzureCredentialFactory _credentials;

    public AzureBlobFileStore(IAzureCredentialFactory credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task<IReadOnlyList<FileRef>> ListAsync(string location, FileDiscovery discovery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var loc = AzureBlobLocation.Parse(location);
        var pattern = string.IsNullOrWhiteSpace(discovery.Pattern) ? "*" : discovery.Pattern;
        var filter = discovery.Filter;

        try
        {
            var container = ContainerClient(loc);

            // A location that points straight at a blob is a single file (mirrors the local store's File.Exists path).
            if (loc.BlobPath.Length > 0)
            {
                var blob = container.GetBlobClient(loc.BlobPath);
                try
                {
                    var props = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
                    var single = ToRef(loc, loc.BlobPath, props.Value.ContentLength, props.Value.LastModified);
                    return filter is null || filter.Includes(single) ? [single] : [];
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Not a blob: treat the path as a virtual directory prefix and enumerate under it below.
                }
            }

            var prefix = loc.BlobPath.Length == 0 ? string.Empty : loc.BlobPath.TrimEnd('/') + "/";
            var results = new List<FileRef>();
            await WalkAsync(container, loc, prefix, pattern, discovery.Recursive, filter, results, ct).ConfigureAwait(false);
            results.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
            return results;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    public async Task<Stream> OpenReadAsync(FileRef file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var loc = AzureBlobLocation.Parse(file.Path);
        try
        {
            var blob = ContainerClient(loc).GetBlobClient(loc.BlobPath);
            var response = await blob.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
            return response.Value.Content;
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw Translate(loc, ex);
        }
    }

    /// <summary>
    /// Turns an Azure SDK failure into an actionable <see cref="SqlFlowException"/>: an auth failure points at
    /// <c>az login</c> / the service-principal variables, a request failure names the account, container, and HTTP
    /// status, and anything else carries the account, container, and underlying cause. The retry wrapper's
    /// <see cref="AggregateException"/> is unwrapped to the informative inner cause.
    /// </summary>
    private static SqlFlowException Translate(AzureBlobLocation loc, Exception ex)
    {
        var cause = Unwrap(ex);
        return cause switch
        {
            AuthenticationFailedException or CredentialUnavailableException => new SqlFlowException(
                $"Azure authentication failed for storage account '{loc.Account}'. Sign in with 'az login', or set "
                + "SQLFLOW_AZURE_AUTH and the AZURE_* variables for a service principal or managed identity "
                + $"({cause.Message}).", ex),
            RequestFailedException rfe => new SqlFlowException(
                $"Azure Storage request failed for '{loc.Account}/{loc.Container}' (status {rfe.Status}): {rfe.Message}", ex),
            _ => new SqlFlowException(
                $"Could not access Azure Storage location '{loc.Account}/{loc.Container}': {cause.Message}", ex),
        };
    }

    private static Exception Unwrap(Exception ex)
        => ex is AggregateException { InnerException: { } inner } ? Unwrap(inner) : ex;

    /// <summary>
    /// Enumerates one virtual-directory level under <paramref name="prefix"/>: blobs whose name matches the glob
    /// are added (after the per-file filter), and each sub-prefix is descended only when recursing and the filter
    /// admits it - so an out-of-window partition folder is skipped without being listed.
    /// </summary>
    private static async Task WalkAsync(
        BlobContainerClient container,
        AzureBlobLocation loc,
        string prefix,
        string pattern,
        bool recursive,
        IFileDiscoveryFilter? filter,
        List<FileRef> sink,
        CancellationToken ct)
    {
        // Sub-prefixes to descend are collected during enumeration and walked afterwards, so the page enumerator is
        // not held open across the recursive listing calls.
        var subDirectories = new List<string>();
        var pageable = container.GetBlobsByHierarchyAsync(delimiter: "/", prefix: prefix, cancellationToken: ct);
        await foreach (var entry in pageable.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsBlob)
            {
                var name = LastSegment(entry.Blob.Name);
                // A hierarchical-namespace directory can surface as a zero-length blob whose name ends in '/'; it
                // has no file name, so the glob match below also excludes it.
                if (name.Length == 0 || !FileSystemName.MatchesSimpleExpression(pattern, name))
                {
                    continue;
                }

                var reference = ToRef(
                    loc,
                    entry.Blob.Name,
                    entry.Blob.Properties.ContentLength ?? 0,
                    entry.Blob.Properties.LastModified);

                if (filter is null || filter.Includes(reference))
                {
                    sink.Add(reference);
                }
            }
            else if (recursive && entry.IsPrefix
                && (filter is null || filter.ShouldEnterDirectory(loc.UriFor(entry.Prefix))))
            {
                subDirectories.Add(entry.Prefix);
            }
        }

        foreach (var subDirectory in subDirectories)
        {
            await WalkAsync(container, loc, subDirectory, pattern, recursive, filter, sink, ct).ConfigureAwait(false);
        }
    }

    private BlobContainerClient ContainerClient(AzureBlobLocation loc)
    {
        var service = new BlobServiceClient(loc.BlobServiceEndpoint, _credentials.Create());
        return service.GetBlobContainerClient(loc.Container);
    }

    private static FileRef ToRef(AzureBlobLocation loc, string blobName, long size, DateTimeOffset? modified) => new()
    {
        Path = loc.UriFor(blobName),
        Name = LastSegment(blobName),
        Size = size,
        Modified = modified,
    };

    private static string LastSegment(string blobName)
    {
        var trimmed = blobName.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
