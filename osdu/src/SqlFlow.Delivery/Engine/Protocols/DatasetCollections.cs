using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Web;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The files of a file collection, put under the staging directory the Dataset service handed out, the way the provider
/// that signed it takes them (osdu/specs/core/INTEGRATION.md section 2.5.1). Every upload goes to a location the
/// storage instructions signed, so the flow's auth and headers never go with it, and the signed location is never
/// logged, stored or returned.
/// <list type="bullet">
/// <item>Azure (<c>signedUrl</c>, a directory SAS on the Data Lake host): each file created, appended and flushed under
/// the directory, as the File service's own Azure test uploads a collection with the Data Lake client (project 90 at
/// <c>d7c25c2d7f5d2f42bed901c68a407098195389bb</c>, <c>testing/file-test-azure/.../Helper/DataLakeHelper.java:15-26</c>).</item>
/// <item>Core-plus on MinIO or S3 (<c>url</c> and <c>signingOptions</c> holding a presigned POST policy): each file sent
/// as a multipart form with the policy's fields and its key under the directory (project 1441 at
/// <c>c389fbea3de45d24c4f4847107dd3057bc988724</c>, <c>MinioFullFlowIT.java:64-98</c>, <c>S3FullFlowIT.java:73-102</c>).</item>
/// <item>Core-plus on Google Cloud Storage (<c>signingOptions</c> holding a bucket, a folder and a token scoped to it):
/// each file sent as a media upload under the folder (project 1475 at <c>abb8463cb22db1d1314452d421387a04cafc7407</c>,
/// <c>GcsFullFlowIT.java:53-61, 93-116</c>).</item>
/// </list>
/// IBM hands out temporary credentials for an object store endpoint it does not name, so a collection there is not
/// uploaded here, and the record says why.
/// </summary>
internal static class DatasetCollections
{
    /// <summary>The largest part of a file one Data Lake append carries; a larger file goes in several.</summary>
    public const long AppendBytes = 100L * 1024 * 1024;

    /// <summary>The Data Lake service version a request states when its SAS names none.</summary>
    public const string DefaultAzureVersion = "2021-06-08";

    /// <summary>The Google Cloud Storage media upload endpoint the OSDU driver tests upload a collection's files to.</summary>
    public const string GcsUploadEndpoint = "https://storage.googleapis.com/upload/storage/v1/b/";

    public static async Task<StagedDataset> UploadAsync(
        OsduHttpClient client, ProtocolOptions options, DatasetStorage storage, IPayloadSource source, IReadOnlyList<PayloadFile> chunks, IReadOnlyList<string> names, CancellationToken ct, long appendBytes = AppendBytes)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfLessThan(appendBytes, 1);
        if (names.Count != chunks.Count)
        {
            throw new ArgumentException("every payload file needs its name", nameof(names));
        }

        // The way to upload is read from the location first: a location with temporary credentials (IBM) names neither
        // an endpoint nor a directory, and holding the record says why, where a missing directory alone would not.
        var signing = storage.Location["signingOptions"] as JsonObject;
        var uploader = storage.Text("signedUrl") is not null ? Uploader.DataLake
            : signing is not null && Text(signing, "policy") is not null ? Uploader.PostPolicy
            : signing is not null && Text(signing, "connectionString") is not null ? Uploader.Google
            : throw new RecordHeldException(
                $"the dataset service handed out a collection location this route cannot upload to (provider {storage.ProviderKey ?? "unnamed"}, with {DatasetUploads.Keys(storage)}); "
                + "a location with temporary credentials names no endpoint to use them with (osdu/specs/core/INTEGRATION.md section 2.5.1)");
        var directory = storage.Text("fileCollectionSource")
            ?? throw new DeliveryException("the dataset service's storage location for a file collection names no fileCollectionSource to register it under.");
        var files = new List<(string Name, long Size)>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var name = names[i];
            switch (uploader)
            {
                case Uploader.DataLake:
                    await AzureAsync(client, DatasetUploads.SignedUrl(storage, "signedUrl"), name, source, chunk, appendBytes, ct).ConfigureAwait(false);
                    break;
                case Uploader.PostPolicy:
                    await PostPolicyAsync(client, storage, signing!, directory, name, source, chunk, options.PayloadContentType, ct).ConfigureAwait(false);
                    break;
                default:
                    await GoogleAsync(client, signing!, name, source, chunk, options.PayloadContentType, ct).ConfigureAwait(false);
                    break;
            }

            files.Add((name, chunk.Size));
        }

        // Azure names the directory with its leading slash; core-plus names it bare, and its copy reads the path as the
        // text after the bucket, so the path is given its slashes (osdu/specs/core/INTEGRATION.md section 2.5.1).
        var path = directory.StartsWith('/') ? directory : "/" + directory.TrimEnd('/') + "/";
        return new StagedDataset(null, path, files);
    }

    /// <summary>
    /// One file under an Azure Data Lake directory SAS: created (<c>PUT ?resource=file</c>), appended in parts
    /// (<c>PATCH ?action=append&amp;position=</c>) and flushed (<c>PATCH ?action=flush&amp;position=</c>), as the Data Lake
    /// client the File service's test uses sends them (Azure Data Lake SDK 12.27.0, <c>PathsImpl.java:100-133, 750-875</c>).
    /// The request states the service version the SAS was signed with. The file is created over whatever a failed try
    /// left, since the directory is new for every try.
    /// </summary>
    private static async Task AzureAsync(OsduHttpClient client, Uri directory, string name, IPayloadSource source, PayloadFile chunk, long appendBytes, CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(directory.Query);
        var version = query["sv"] ?? DefaultAzureVersion;
        var file = FileUrl(directory, name);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-version"] = version };
        await client.SendToSignedUrlAsync(HttpMethod.Put, WithQuery(file, "resource=file"), () => new MemoryStream([], writable: false), "application/octet-stream", 0, headers, ct).ConfigureAwait(false);

        var position = 0L;
        while (position < chunk.Size)
        {
            var length = Math.Min(appendBytes, chunk.Size - position);
            var offset = position;
            await client.SendToSignedUrlAsync(
                new HttpMethod("PATCH"),
                WithQuery(file, "action=append&position=" + offset.ToString(CultureInfo.InvariantCulture)),
                () => SegmentStream.Open(source, chunk, offset, length),
                "application/octet-stream",
                length,
                headers,
                ct).ConfigureAwait(false);
            position += length;
        }

        await client.SendToSignedUrlAsync(
            new HttpMethod("PATCH"),
            WithQuery(file, "action=flush&position=" + chunk.Size.ToString(CultureInfo.InvariantCulture)),
            () => new MemoryStream([], writable: false),
            "application/octet-stream",
            0,
            headers,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One file sent with a presigned POST policy (MinIO, S3): the policy's fields, the key the file takes under the
    /// directory, and the file last, as the OSDU object store driver tests send it. MinIO's fields name no key, and the
    /// key is the directory and the name; S3's name the directory, and the name is appended to it.
    /// </summary>
    private static async Task PostPolicyAsync(
        OsduHttpClient client, DatasetStorage storage, JsonObject signing, string directory, string name, IPayloadSource source, PayloadFile chunk, string contentType, CancellationToken ct)
    {
        var target = DatasetUploads.SignedUrl(storage, "url");
        var fields = new List<KeyValuePair<string, string>>();
        string? key = null;
        foreach (var (field, value) in signing)
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var content))
            {
                continue;
            }

            if (string.Equals(field, "key", StringComparison.Ordinal))
            {
                key = content + name;
                continue;
            }

            fields.Add(new(field, content));
        }

        fields.Add(new("key", key ?? directory.Trim('/') + "/" + name));
        await client.SendFormToSignedUrlAsync(target, fields, "file", name, () => OsduWellLogProtocol.OpenSync(source, chunk), chunk.Size, contentType, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One file sent as a Google Cloud Storage media upload under the folder the downscoped token covers, as the OSDU
    /// Google driver test sends it; the token is the one the storage instructions scoped to the folder.
    /// </summary>
    private static async Task GoogleAsync(OsduHttpClient client, JsonObject signing, string name, IPayloadSource source, PayloadFile chunk, string contentType, CancellationToken ct)
    {
        var bucket = Text(signing, "bucket") ?? throw new DeliveryException("the dataset service's Google storage location names no bucket.");
        var folder = Text(signing, "filepath") ?? string.Empty;
        var token = Text(signing, "connectionString")!;
        var url = new Uri(GcsUploadEndpoint + Uri.EscapeDataString(bucket) + "/o?uploadType=media&name=" + Uri.EscapeDataString(folder + name));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token };
        await client.SendToSignedUrlAsync(HttpMethod.Post, url, () => OsduWellLogProtocol.OpenSync(source, chunk), contentType, chunk.Size, headers, ct).ConfigureAwait(false);
    }

    /// <summary>The URL of a file under a directory URL, keeping the directory's query (its SAS).</summary>
    private static Uri FileUrl(Uri directory, string name)
    {
        var builder = new UriBuilder(directory) { Path = directory.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(name) };
        return builder.Uri;
    }

    private static Uri WithQuery(Uri url, string parameters)
    {
        var builder = new UriBuilder(url);
        var query = builder.Query.TrimStart('?');
        builder.Query = parameters + (query.Length == 0 ? string.Empty : "&" + query);
        return builder.Uri;
    }

    private static string? Text(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

    /// <summary>How a collection location takes its files.</summary>
    private enum Uploader
    {
        /// <summary>A Data Lake directory SAS (Azure).</summary>
        DataLake,

        /// <summary>A presigned POST policy (MinIO, S3).</summary>
        PostPolicy,

        /// <summary>A token scoped to a Google Cloud Storage folder.</summary>
        Google,
    }
}

/// <summary>
/// A read-only view of one part of a payload file, opened fresh for every attempt: a seekable file is positioned at the
/// part, any other is read up to it. At most the part's length is read.
/// </summary>
internal sealed class SegmentStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    private SegmentStream(Stream inner, long length)
    {
        _inner = inner;
        _remaining = length;
    }

    public static Stream Open(IPayloadSource source, PayloadFile chunk, long offset, long length)
    {
        var inner = OsduWellLogProtocol.OpenSync(source, chunk);
        try
        {
            if (offset > 0)
            {
                if (inner.CanSeek)
                {
                    inner.Seek(offset, SeekOrigin.Begin);
                }
                else
                {
                    Skip(inner, offset);
                }
            }

            return new SegmentStream(inner, length);
        }
        catch
        {
            inner.Dispose();
            throw;
        }
    }

    private static void Skip(Stream stream, long count)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
            {
                throw new DeliveryException("a payload file ended before the part an upload was sending");
            }

            count -= read;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
