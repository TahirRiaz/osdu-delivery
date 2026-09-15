using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Storage;

namespace SqlFlow.Delivery.Storage;

/// <summary>
/// One file a caller may upload straight into storage: where it will land, the URL that authorises writing it there,
/// and when that authorisation stops working. The URL carries its own credential, so it is handed to the caller and
/// never logged, stored or echoed back in an error.
/// </summary>
/// <param name="Location">The location the file will occupy, which is what a submission points at afterwards.</param>
/// <param name="Url">The write-only URL, valid until <paramref name="ExpiresUtc"/>.</param>
/// <param name="ExpiresUtc">When the URL stops working.</param>
public sealed record SignedUpload(string Location, Uri Url, DateTimeOffset ExpiresUtc);

/// <summary>
/// Hands out a URL that authorises writing one file, and nothing else, for the storage families that can issue one.
/// <para>
/// It exists because a file large enough to matter should not travel through the control plane twice (in on the
/// request, out to storage) just to be hashed on the way: the caller writes it where it belongs, and the control
/// plane records what landed. Everything the delivery itself does is unchanged, since the run reads the file from
/// storage with the node's own identity exactly as it reads a prepared drop.
/// </para>
/// <para>
/// The URL is write-only and scoped to one blob: it cannot read, list or delete, and it cannot reach a second file.
/// That is what keeps a reservation from becoming a general grant on the drop-off area.
/// </para>
/// </summary>
public interface ISignedUploadIssuer
{
    bool CanHandle(string location);

    /// <summary>
    /// A URL authorising a write to <paramref name="location"/> for <paramref name="lifetime"/>.
    /// </summary>
    /// <exception cref="DeliveryException">The URL could not be issued (the identity may not delegate, the account is unreachable).</exception>
    Task<SignedUpload> CreateUploadAsync(string location, TimeSpan lifetime, CancellationToken ct = default);
}

/// <summary>
/// Azure Storage signed uploads, as user delegation SAS: signed by a key the control plane's own identity asks the
/// service for, so no account key exists anywhere in the deployment and every URL is traceable to that identity in
/// the storage account's own logs.
/// <para>
/// The identity needs <c>Storage Blob Delegator</c> on the account on top of its data role. Without it the service
/// refuses to issue a delegation key, which surfaces here as a failure naming the missing grant rather than as an
/// opaque 403 the caller sees when they try to upload.
/// </para>
/// </summary>
public sealed class AzureBlobSignedUploadIssuer : ISignedUploadIssuer
{
    /// <summary>
    /// How long before a delegation key expires it stops being handed out. A key is valid for a window, and a URL
    /// signed with one cannot outlive it, so the margin keeps a URL issued at the last moment usable for its whole
    /// declared lifetime rather than being cut short by the key behind it.
    /// </summary>
    private static readonly TimeSpan KeyMargin = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a delegation key is asked for. Long enough that a burst of reservations shares one key, short enough
    /// that a key is never worth persisting anywhere.
    /// </summary>
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// The clock skew a URL starts before, so a caller whose clock runs behind the service does not meet a SAS that is
    /// not valid yet.
    /// </summary>
    private static readonly TimeSpan StartSkew = TimeSpan.FromMinutes(5);

    private readonly IAzureCredentialFactory _credentials;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CachedKey> _keys = new(StringComparer.OrdinalIgnoreCase);

    public AzureBlobSignedUploadIssuer(IAzureCredentialFactory credentials, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(time);
        _credentials = credentials;
        _time = time;
    }

    public bool CanHandle(string location) => AzureBlobLocation.IsAzureStorageUri(location);

    public async Task<SignedUpload> CreateUploadAsync(string location, TimeSpan lifetime, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A signed upload's lifetime is positive.");
        }

        var parsed = AzureBlobLocation.Parse(location);
        if (string.IsNullOrEmpty(parsed.BlobPath))
        {
            throw new DeliveryException($"'{location}' names a container, not a file; a signed upload is issued for one file.");
        }

        var now = _time.GetUtcNow();
        var expiresOn = now + lifetime;
        var service = new BlobServiceClient(parsed.BlobServiceEndpoint, _credentials.Create());
        var key = await DelegationKeyAsync(service, parsed.Account, now, expiresOn, ct).ConfigureAwait(false);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = parsed.Container,
            BlobName = parsed.BlobPath,
            Resource = "b",
            StartsOn = now - StartSkew,
            ExpiresOn = expiresOn,
            Protocol = SasProtocol.Https,
        };
        // Write and Create, and deliberately nothing else: the holder may put this one blob's content there and can
        // neither read it back, discover its neighbours, nor remove anything.
        builder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

        var url = new BlobUriBuilder(service.Uri)
        {
            BlobContainerName = parsed.Container,
            BlobName = parsed.BlobPath,
            Sas = builder.ToSasQueryParameters(key, parsed.Account),
        }.ToUri();
        return new SignedUpload(location, url, expiresOn);
    }

    /// <summary>
    /// The account's user delegation key, reused while it comfortably covers <paramref name="expiresOn"/>. A key is a
    /// signing credential: it is held in memory for its window only and never written anywhere.
    /// </summary>
    private async Task<UserDelegationKey> DelegationKeyAsync(
        BlobServiceClient service, string account, DateTimeOffset now, DateTimeOffset expiresOn, CancellationToken ct)
    {
        if (_keys.TryGetValue(account, out var cached) && cached.Covers(now, expiresOn, KeyMargin))
        {
            return cached.Key;
        }

        // The key must outlive every URL signed with it, so it is asked for to cover whichever is later: the standard
        // key lifetime, or this URL's own expiry plus the margin.
        var keyExpiry = Later(now + KeyLifetime, expiresOn + KeyMargin);
        UserDelegationKey key;
        try
        {
            var response = await service.GetUserDelegationKeyAsync(now - StartSkew, keyExpiry, ct).ConfigureAwait(false);
            key = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw new DeliveryException(
                $"Storage account '{account}' refused a user delegation key to this identity (status {ex.Status}). Signed uploads need the "
                + "'Storage Blob Delegator' role on the account, in addition to the data role that lets it write. Grant it, or upload through "
                + "the control plane instead.", ex);
        }
        catch (RequestFailedException ex)
        {
            throw new DeliveryException($"Could not get a user delegation key for storage account '{account}' (status {ex.Status}): {ex.Message}", ex);
        }

        _keys[account] = new CachedKey(key, keyExpiry);
        return key;
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private sealed record CachedKey(UserDelegationKey Key, DateTimeOffset ExpiresOn)
    {
        public bool Covers(DateTimeOffset now, DateTimeOffset until, TimeSpan margin)
            => ExpiresOn > now && ExpiresOn >= until + margin;
    }
}
