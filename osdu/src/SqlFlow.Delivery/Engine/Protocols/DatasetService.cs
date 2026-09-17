using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>What the Dataset service handed out for storing a dataset: where to put its files, and which provider holds them.</summary>
/// <param name="Location">The <c>storageLocation</c> object, which the contract leaves untyped and each provider shapes its own way.</param>
/// <param name="ProviderKey">The provider the location belongs to (<c>AZURE</c>, or the core-plus provider's name).</param>
public sealed record DatasetStorage(JsonObject Location, string? ProviderKey)
{
    /// <summary>A text property of the location, or null.</summary>
    public string? Text(string name) => Location[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}

/// <summary>
/// The Dataset service calls the dataset and workflow routes make (openapi dataset v1; osdu/specs/core/INTEGRATION.md
/// sections 2.5 and 3.3): a storage location for a dataset kind, the upload of each file to it, the registration of
/// dataset records (the service keeps an id it is given when the id's entity type is the kind's, and copies the staged
/// files to their lasting place), the retrieval instructions that show a registered dataset's files can be read, and
/// the reversible removal of a dataset record.
/// </summary>
public sealed class DatasetService
{
    public const string DefaultInstructionsPath = "/api/dataset/v1/storageInstructions";
    public const string DefaultRegisterPath = "/api/dataset/v1/registerDataset";
    public const string DefaultRetrievalPath = "/api/dataset/v1/retrievalInstructions";
    public const string DefaultSoftDeletePath = "/api/dataset/v1/metadataRecord/{id}/softDelete";
    public const string DefaultProbePath = "/api/dataset/v1/info";

    /// <summary>Records one registration takes, and ids one retrieval takes ("Only 20 Dataset Registries can be ingested at a time").</summary>
    public const int MaxRecords = 20;

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;

    public DatasetService(OsduHttpClient client, ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options;
    }

    /// <summary>True for an entity type the Dataset service registers: the <c>dataset</c> group.</summary>
    public static bool IsDatasetType(string entityType)
        => entityType.StartsWith("dataset--", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a file collection entity type, whose files live under one directory the dataset names.</summary>
    public static bool IsCollectionType(string entityType)
        => entityType.StartsWith("dataset--FileCollection.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A storage location for a dataset of <paramref name="entityType"/> (openapi dataset v1, POST storageInstructions
    /// with the required <c>kindSubType</c>). The Dataset service does not pass <c>expiryTime</c> on to the file service
    /// behind it, so none is sent (osdu/specs/core/INTEGRATION.md section 2.5): a location is valid for the file
    /// service's default hour. 400 means no DMS serves the type, 405 that the DMS takes no storage; both hold the record.
    /// </summary>
    public async Task<DatasetStorage> StorageAsync(string entityType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        var url = OsduHttpClient.WithQuery(_client.Url(_options.DatasetInstructionsPath ?? DefaultInstructionsPath), "kindSubType", entityType);
        HttpFetchResult result;
        try
        {
            // Repeated on an unclear outcome: a call changes nothing and only signs a new location.
            result = await _client.SendJsonAsync(HttpMethod.Post, url, null, null, ct, idempotent: true).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode is 400 or 405)
        {
            throw new RecordHeldException(
                ex.StatusCode == 400
                    ? $"the dataset service has no storage for {entityType} (no DMS serves the type on this platform): {HeaderRedaction.RedactMessage(ex.Message)}"
                    : $"the DMS serving {entityType} does not store files: {HeaderRedaction.RedactMessage(ex.Message)}",
                ex);
        }

        if (JsonNode.Parse(result.Body) is not JsonObject root || root["storageLocation"] is not JsonObject location)
        {
            throw new DeliveryException($"{url.AbsolutePath} answered without a storageLocation object.");
        }

        var provider = root["providerKey"] is JsonValue key && key.TryGetValue<string>(out var text) ? text : null;
        return new DatasetStorage((JsonObject)location.DeepClone(), provider);
    }

    /// <summary>
    /// Registers dataset records (openapi dataset v1, PUT registerDataset, 1 to 20 per request) and returns the version
    /// each landed at, by id, from the records the service answers with (it reads them back from storage). The request is
    /// repeated on an unclear outcome: every record carries its own id, so a repeat lands on the same records.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long?>> RegisterAsync(IReadOnlyList<JsonObject> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        var landed = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var chunk in records.Chunk(MaxRecords))
        {
            if (chunk.Any(r => r["id"] is null))
            {
                throw new DeliveryException("a dataset record is registered under an id of its own, so a repeated registration lands on the same record; one has none");
            }

            var url = _client.Url(_options.DatasetRegisterPath ?? DefaultRegisterPath);
            var body = new JsonObject { ["datasetRegistries"] = new JsonArray(chunk.Select(r => (JsonNode?)r.DeepClone()).ToArray()) };
            var result = await _client.SendJsonAsync(HttpMethod.Put, url, body, null, ct, idempotent: true).ConfigureAwait(false);
            var root = OsduHttpClient.ParseJson(result, url);
            foreach (var record in JsonPathReader.SelectElements(root, "datasetRegistries[*]"))
            {
                if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    landed[id.GetString()!] = RecordWriter.ParseVersion(JsonPathReader.SelectValue(record, "version"));
                }
            }

            foreach (var record in chunk)
            {
                var id = record["id"]!.GetValue<string>();
                if (!landed.ContainsKey(id))
                {
                    throw new DeliveryException(
                        $"{url.AbsolutePath} accepted the registration of {id} and did not answer with it; the service leaves out a record it cannot read back, so the caller may not see it");
                }
            }
        }

        return landed;
    }

    /// <summary>
    /// The ids among <paramref name="ids"/> the Dataset service answers retrieval instructions for (openapi dataset v1,
    /// POST retrievalInstructions, 1 to 20 ids per request): the datasets whose files it can hand out.
    /// </summary>
    public async Task<IReadOnlySet<string>> RetrievableAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(MaxRecords))
        {
            var url = _client.Url(_options.DatasetRetrievalPath ?? DefaultRetrievalPath);
            var body = new JsonObject { ["datasetRegistryIds"] = new JsonArray(chunk.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
            var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
            foreach (var id in JsonPathReader.SelectValues(OsduHttpClient.ParseJson(result, url), "datasets[*].datasetRegistryId"))
            {
                found.Add(id);
            }
        }

        return found;
    }

    /// <summary>
    /// Where one registered dataset's files can be read (openapi dataset v1, POST retrievalInstructions), as the
    /// <c>retrievalProperties</c> object the DMS answers, or null when the service lists no dataset for the id.
    /// </summary>
    public async Task<JsonObject?> RetrievalAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var url = _client.Url(_options.DatasetRetrievalPath ?? DefaultRetrievalPath);
        var body = new JsonObject { ["datasetRegistryIds"] = new JsonArray(JsonValue.Create(id)) };
        var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404 || JsonNode.Parse(result.Body) is not JsonObject root)
        {
            return null;
        }

        foreach (var dataset in root["datasets"] as JsonArray ?? [])
        {
            if (dataset is JsonObject item
                && item["datasetRegistryId"] is JsonValue registered
                && registered.TryGetValue<string>(out var registeredId)
                && string.Equals(registeredId, id, StringComparison.Ordinal))
            {
                return item["retrievalProperties"] as JsonObject;
            }
        }

        return null;
    }

    /// <summary>
    /// The Dataset service's reversible removal of a dataset record (openapi dataset v1, POST
    /// metadataRecord/{id}/softDelete): it keeps a copy its undelete restores, then deletes the record logically in
    /// storage; 204. A record already gone (404) is reported as such.
    /// </summary>
    public async Task<DeleteOutcome> SoftDeleteAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var url = _client.Url(_options.DatasetSoftDeletePath ?? DefaultSoftDeletePath, id);
        var result = await _client.SendJsonAsync(HttpMethod.Post, url, null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        return (int)result.Status == 404
            ? new DeleteOutcome(false, true, "dataset record not found in OSDU")
            : new DeleteOutcome(true, false, "removed from OSDU through the dataset service (reversible: its undelete restores it)");
    }

    /// <summary>The file size text the dataset schemas take (<c>^[0-9]+$</c>).</summary>
    public static string Size(long bytes) => bytes.ToString(CultureInfo.InvariantCulture);
}
