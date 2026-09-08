using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The plain-record protocol (design.md section 8.1): one JSON document, upsert by client-supplied id through the
/// storage service's array endpoint. The write response's <c>recordIdVersions</c> entry carries <c>id:version</c>.
/// </summary>
public sealed class OsduRecordProtocol : IDeliveryProtocol
{
    public const string DefaultRecordPath = "/api/storage/v2/records";
    public const string DefaultVerifyPath = "/api/storage/v2/records/{id}";
    public const string DefaultProbePath = "/api/storage/v2/info";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;

    public OsduRecordProtocol(OsduHttpClient client, ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduRecord;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!work.DeliverMetadata)
        {
            return new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion };
        }

        var version = await RecordWriter.WriteAsync(_client, _options, _options.RecordPath ?? DefaultRecordPath, _options.RecordMethod ?? "PUT", _options.VerifyPath ?? DefaultVerifyPath, work, ct).ConfigureAwait(false);
        return new DeliveryOutcome { MetadataDelivered = true, PayloadDelivered = false, TargetVersion = version };
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? DefaultProbePath, ct);

    /// <summary>
    /// Storage service semantics (openapi storage v2): <c>POST /records/{id}:delete</c> is the logical, revertible
    /// delete; <c>DELETE /records/{id}</c> purges the record and all its versions and cannot be undone. Both answer 204.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, bool purge, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = purge
            ? _client.Url(_options.PurgePath ?? DefaultPurgePath, targetId)
            : _client.Url(_options.DeletePath ?? DefaultDeletePath, targetId);
        var method = purge ? HttpMethod.Delete : HttpMethod.Post;
        var result = await _client.SendJsonAsync(method, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        return (int)result.Status == 404
            ? new DeleteOutcome(false, true, "record not found in OSDU")
            : new DeleteOutcome(true, false, purge ? "purged (all versions)" : "logically deleted");
    }

    public const string DefaultDeletePath = "/api/storage/v2/records/{id}:delete";
    public const string DefaultPurgePath = "/api/storage/v2/records/{id}";
}

/// <summary>The record write and read-back shared by the two implemented protocols.</summary>
internal static class RecordWriter
{
    public static async Task<long?> WriteAsync(OsduHttpClient client, ProtocolOptions options, string recordPath, string method, string verifyPath, DeliveryWork work, CancellationToken ct)
    {
        var document = (JsonObject)work.Document.DeepClone();
        if (options.PreserveDataKeys.Count > 0 && work.ExistingVersion is not null)
        {
            await PreserveAsync(client, verifyPath, work.TargetId, document, options.PreserveDataKeys, ct).ConfigureAwait(false);
        }

        var url = client.Url(recordPath);
        var body = new JsonArray(document);
        var result = await client.SendJsonAsync(new HttpMethod(method.ToUpperInvariant()), url, body, null, ct).ConfigureAwait(false);
        if (result.Body.Length == 0)
        {
            return null;
        }

        var root = OsduHttpClient.ParseJson(result, url);
        var idVersion = JsonPathReader.SelectValue(root, options.VersionPath);
        return ParseVersion(idVersion);
    }

    public static async Task<VerifyResult> VerifyAsync(OsduHttpClient client, string verifyPath, string targetId, long? expectedVersion, CancellationToken ct)
    {
        var url = client.Url(verifyPath, targetId);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return new VerifyResult(VerifyOutcome.Missing, null, "record not found");
        }

        var root = OsduHttpClient.ParseJson(result, url);
        var observed = ParseVersion(JsonPathReader.SelectValue(root, "version"));
        if (observed is null)
        {
            return new VerifyResult(VerifyOutcome.Error, null, "record has no version");
        }

        if (expectedVersion is null)
        {
            return new VerifyResult(VerifyOutcome.Match, observed, "no expected version recorded; observed version adopted");
        }

        return observed == expectedVersion
            ? new VerifyResult(VerifyOutcome.Match, observed, null)
            : new VerifyResult(VerifyOutcome.Drifted, observed, $"observed version {observed}, ledger holds {expectedVersion}");
    }

    /// <summary>Reads a record back as the target holds it; null on 404.</summary>
    public static async Task<JsonObject?> ReadAsync(OsduHttpClient client, string verifyPath, string targetId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var url = client.Url(verifyPath, targetId);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return null;
        }

        return JsonNode.Parse(result.Body) as JsonObject
            ?? throw new DeliveryException($"{url.AbsolutePath}: the target answered with something other than a JSON record.");
    }

    /// <summary>
    /// Calls the service's info endpoint under the flow's auth. A refused or failed call is an outcome, not an
    /// exception: the probe exists to report exactly that. Messages are redacted before they leave.
    /// </summary>
    public static async Task<ProbeOutcome> ProbeAsync(OsduHttpClient client, string probePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var url = client.Url(probePath);
        try
        {
            var result = await client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
            return new ProbeOutcome(true, (int)result.Status, "the service answered", url.AbsolutePath);
        }
        catch (HttpStatusException ex)
        {
            return new ProbeOutcome(false, ex.StatusCode, HeaderRedaction.RedactMessage(ex.Message), url.AbsolutePath);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ProbeOutcome(false, 0, HeaderRedaction.RedactMessage(ex.Message), url.AbsolutePath);
        }
    }

    /// <summary>Copies the OSDU-owned data keys (design.md section 7.6) from the current record into the document.</summary>
    private static async Task PreserveAsync(OsduHttpClient client, string verifyPath, string targetId, JsonObject document, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var url = client.Url(verifyPath, targetId);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return;
        }

        var existing = JsonNode.Parse(result.Body) as JsonObject;
        if (existing?["data"] is not JsonObject existingData)
        {
            return;
        }

        if (document["data"] is not JsonObject data)
        {
            data = new JsonObject();
            document["data"] = data;
        }

        foreach (var key in keys)
        {
            if (existingData[key] is { } value)
            {
                data[key] = value.DeepClone();
            }
        }
    }

    public static long? ParseVersion(string? idVersion)
    {
        if (string.IsNullOrWhiteSpace(idVersion))
        {
            return null;
        }

        var last = idVersion.LastIndexOf(':');
        var text = last >= 0 && last < idVersion.Length - 1 ? idVersion[(last + 1)..] : idVersion;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
