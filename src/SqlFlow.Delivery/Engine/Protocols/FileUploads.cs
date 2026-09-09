using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>One payload chunk after its upload: what it is called, how long it is, and where the landing zone keeps it.</summary>
internal sealed record UploadedFile(int Index, string Name, long Size, string FileSource, string? FileId);

/// <summary>
/// The file service half the file and manifest protocols share (openapi file v2). Per chunk, <c>GET files/uploadURL</c>
/// hands out a signed landing-zone location and the FileSource path the file keeps; the chunk streams to the signed
/// URL with its length (the URL carries its own authorisation, so only the declared upload headers go with it); the
/// file protocol then registers the dataset record against that FileSource through <c>POST files/metadata</c>.
/// Every chunk is its own resumable step (design.md section 16.3): a retry after the third of five uploads sends
/// two, not five, and a retry after a failed registration registers the file it already uploaded.
/// </summary>
internal static class FileUploads
{
    public const string DefaultUploadUrlPath = "/api/file/v2/files/uploadURL";
    public const string DefaultFileMetadataPath = "/api/file/v2/files/metadata";
    public const string DefaultFileDeletePath = "/api/file/v2/files/{id}/metadata";
    public const string DefaultFileProbePath = "/api/file/v2/info";

    /// <summary>The target-state value listing the dataset record ids the record's files became, comma separated.</summary>
    public const string DatasetIdsValue = "datasetIds";

    public static string UploadStep(int index) => "upload-" + index.ToString(CultureInfo.InvariantCulture);

    public static string RegisterStep(int index) => "register-" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Lists the payload chunks and checks each against the declared request body ceiling before anything is sent,
    /// so an oversized or empty file holds the record instead of failing after some uploads (design.md section 14.3).
    /// </summary>
    public static async Task<IReadOnlyList<PayloadChunk>> ListChunksAsync(DeliveryWork work, long requestBodyCeiling, CancellationToken ct)
    {
        var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
        var chunks = await payload.ListChunksAsync(ct).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            throw new RecordHeldException("no payload chunk files were found for the record");
        }

        foreach (var chunk in chunks)
        {
            if (chunk.Size <= 0)
            {
                throw new RecordHeldException($"payload chunk {chunk.Index} ({FileName(chunk.Path)}) is empty; an empty file cannot be registered as a dataset");
            }

            if (requestBodyCeiling > 0 && chunk.Size > requestBodyCeiling)
            {
                throw new RecordHeldException(
                    $"payload chunk {chunk.Index} is {chunk.Size.ToString(CultureInfo.InvariantCulture)} bytes, above the target's declared request body ceiling of {requestBodyCeiling.ToString(CultureInfo.InvariantCulture)} bytes (reliability.maxRequestBodyBytes); re-chunk in prepare");
            }
        }

        return chunks;
    }

    /// <summary>Uploads the chunks an earlier try did not, reporting each as a step, and returns where every chunk landed.</summary>
    public static async Task<List<UploadedFile>> UploadAsync(OsduHttpClient client, ProtocolOptions options, DeliveryWork work, IReadOnlyList<PayloadChunk> chunks, DeliverySteps steps, CancellationToken ct)
    {
        var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
        var files = new List<UploadedFile>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var step = UploadStep(chunk.Index);
            var name = FileName(chunk.Path);
            if (work.Completed(step) is { } done && done.TryGetValue("fileSource", out var landed) && !string.IsNullOrEmpty(landed))
            {
                steps.Resumed(step, done);
                files.Add(new UploadedFile(chunk.Index, name, chunk.Size, landed, done.TryGetValue("fileId", out var fileId) ? fileId : null));
                continue;
            }

            var started = steps.Now;
            var location = await UploadLocationAsync(client, options, ct).ConfigureAwait(false);
            var result = await client.SendToSignedUrlAsync(
                HttpMethod.Put,
                location.SignedUrl,
                () => OsduWellLogProtocol.OpenSync(payload, chunk),
                options.PayloadContentType,
                chunk.Size,
                options.UploadHeaders,
                ct).ConfigureAwait(false);
            var returned = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileSource"] = location.FileSource,
                ["name"] = name,
                ["size"] = chunk.Size.ToString(CultureInfo.InvariantCulture),
            };
            if (location.FileId is not null)
            {
                returned["fileId"] = location.FileId;
            }

            steps.Add(step, started, (int)result.Status, returned);
            await work.ReportStepAsync(step, returned, ct).ConfigureAwait(false);
            files.Add(new UploadedFile(chunk.Index, name, chunk.Size, location.FileSource, location.FileId));
        }

        return files;
    }

    /// <summary>Registers the dataset record of an uploaded file (openapi file v2, POST files/metadata) and returns its id.</summary>
    public static async Task<string> RegisterAsync(OsduHttpClient client, ProtocolOptions options, DeliveryWork work, UploadedFile file, DeliverySteps steps, CancellationToken ct)
    {
        var step = RegisterStep(file.Index);
        if (work.Completed(step) is { } done && done.TryGetValue("datasetId", out var known) && !string.IsNullOrEmpty(known))
        {
            steps.Resumed(step, done);
            return known;
        }

        var started = steps.Now;
        var url = client.Url(options.FileMetadataPath ?? DefaultFileMetadataPath);
        var result = await client.SendJsonAsync(HttpMethod.Post, url, DatasetRecord(options, work.Document, file, null), null, ct).ConfigureAwait(false);
        var datasetId = JsonPathReader.SelectValue(OsduHttpClient.ParseJson(result, url), "id");
        if (string.IsNullOrWhiteSpace(datasetId))
        {
            throw new DeliveryException($"{url.AbsolutePath} did not return the id of the dataset record for {file.Name}.");
        }

        var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetId"] = datasetId, ["fileSource"] = file.FileSource };
        steps.Add(step, started, (int)result.Status, returned);
        await work.ReportStepAsync(step, returned, ct).ConfigureAwait(false);
        return datasetId;
    }

    /// <summary>
    /// The dataset record of one uploaded file (openapi file v2, FileMetadata; the same shape the manifest's Datasets
    /// section takes): the flow's ACL and legal tags copied from the rendered record, never restated, and
    /// FileSourceInfo pointing at the landing-zone path the upload returned.
    /// </summary>
    public static JsonObject DatasetRecord(ProtocolOptions options, JsonObject document, UploadedFile file, string? id)
    {
        var acl = document["acl"]?.DeepClone() ?? throw new DeliveryException("the rendered record has no acl block to copy onto its dataset record");
        var legal = document["legal"]?.DeepClone() ?? throw new DeliveryException("the rendered record has no legal block to copy onto its dataset record");
        var size = file.Size.ToString(CultureInfo.InvariantCulture);
        var record = new JsonObject();
        if (id is not null)
        {
            record["id"] = id;
        }

        record["kind"] = options.DatasetKind;
        record["acl"] = acl;
        record["legal"] = legal;
        record["data"] = new JsonObject
        {
            ["Name"] = file.Name,
            ["TotalSize"] = size,
            ["DatasetProperties"] = new JsonObject
            {
                ["FileSourceInfo"] = new JsonObject
                {
                    ["FileSource"] = file.FileSource,
                    ["Name"] = file.Name,
                    ["FileSize"] = size,
                },
            },
        };
        return record;
    }

    /// <summary>Points the record's dataset list at the given ids, keeping any the mapping rendered first, without duplicates.</summary>
    public static void SetDatasets(JsonObject document, string property, IReadOnlyList<string> ids)
    {
        if (document["data"] is not JsonObject data)
        {
            data = new JsonObject();
            document["data"] = data;
        }

        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (data[property] is JsonArray existing)
        {
            foreach (var node in existing)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var text) && seen.Add(text))
                {
                    merged.Add(text);
                }
            }
        }

        foreach (var id in ids)
        {
            if (seen.Add(id))
            {
                merged.Add(id);
            }
        }

        data[property] = new JsonArray(merged.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
    }

    /// <summary>The dataset ids the record's earlier deliveries registered, from its target state.</summary>
    public static IReadOnlyList<string> DatasetIds(IReadOnlyDictionary<string, string>? targetState)
        => targetState is not null && targetState.TryGetValue(DatasetIdsValue, out var joined) && !string.IsNullOrWhiteSpace(joined)
            ? joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>
    /// Storage semantics for the record itself, plus the dataset records the record owns. Only
    /// <see cref="RemovalScope.Everything"/> takes the datasets and their files with it (openapi file v2,
    /// DELETE files/{id}/metadata): the reversible removal leaves them in place so OSDU can restore the record
    /// whole, and a history purge only touches the record's own earlier versions. A dataset already gone is not an
    /// error.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteRecordAndDatasetsAsync(OsduHttpClient client, ProtocolOptions options, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var outcome = await RecordWriter.DeleteAsync(client, RemovalPaths.From(options), targetId, scope, ct).ConfigureAwait(false);
        if (scope != RemovalScope.Everything)
        {
            return outcome;
        }

        var deleted = 0;
        foreach (var id in DatasetIds(targetState))
        {
            var url = client.Url(options.FileDeletePath ?? DefaultFileDeletePath, id);
            var result = await client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
            if ((int)result.Status != 404)
            {
                deleted++;
            }
        }

        return deleted == 0
            ? outcome
            : outcome with { Deleted = true, Detail = $"{outcome.Detail}; {deleted.ToString(CultureInfo.InvariantCulture)} dataset record(s) and their files deleted" };
    }

    /// <summary>The last path segment, the name the dataset record carries.</summary>
    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static async Task<UploadLocation> UploadLocationAsync(OsduHttpClient client, ProtocolOptions options, CancellationToken ct)
    {
        var url = client.Url(options.UploadUrlPath ?? DefaultUploadUrlPath);
        if (!string.IsNullOrEmpty(options.UploadUrlExpiry))
        {
            var builder = new UriBuilder(url);
            var query = builder.Query.TrimStart('?');
            builder.Query = (query.Length == 0 ? string.Empty : query + "&") + "expiryTime=" + Uri.EscapeDataString(options.UploadUrlExpiry);
            url = builder.Uri;
        }

        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
        var root = OsduHttpClient.ParseJson(result, url);
        var signed = JsonPathReader.SelectValue(root, "Location.SignedURL");
        var fileSource = JsonPathReader.SelectValue(root, "Location.FileSource");
        if (string.IsNullOrWhiteSpace(signed) || string.IsNullOrWhiteSpace(fileSource))
        {
            throw new DeliveryException($"{url.AbsolutePath} did not return Location.SignedURL and Location.FileSource.");
        }

        if (!Uri.TryCreate(signed, UriKind.Absolute, out var signedUrl) || (signedUrl.Scheme != Uri.UriSchemeHttps && signedUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new DeliveryException($"{url.AbsolutePath} returned a signed upload URL that is not an absolute http(s) URL.");
        }

        return new UploadLocation(signedUrl, fileSource, JsonPathReader.SelectValue(root, "FileID"));
    }

    /// <summary>The signed URL is a credential: it is used for the one upload and never logged, stored or returned.</summary>
    private sealed record UploadLocation(Uri SignedUrl, string FileSource, string? FileId);
}
