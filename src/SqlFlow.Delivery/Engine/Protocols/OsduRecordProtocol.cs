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
/// The plain-record protocol (design.md section 8.1): JSON documents upserted by client-supplied id through the
/// storage service's array endpoint, up to <see cref="ProtocolOptions.BatchSize"/> records per request. The write
/// response's <c>recordIdVersions</c> entries carry <c>id:version</c> per record and <c>skippedRecordIds</c> the
/// ones the service found unchanged. A batch the service rejects as a whole (a 4xx) is retried record by record,
/// so one bad document holds itself and not its neighbours.
/// </summary>
public sealed class OsduRecordProtocol : IDeliveryProtocol
{
    public const string DefaultRecordPath = "/api/storage/v2/records";
    public const string DefaultVerifyPath = "/api/storage/v2/records/{id}";
    public const string DefaultProbePath = "/api/storage/v2/info";
    public const string DefaultDeletePath = "/api/storage/v2/records/{id}:delete";
    public const string DefaultPurgePath = "/api/storage/v2/records/{id}";

    public const string RecordsStep = "records";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly TimeProvider _time;

    public OsduRecordProtocol(OsduHttpClient client, ProtocolOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduRecord;

    public int MaxBatch => Math.Clamp(_options.BatchSize, 1, ProtocolOptions.MaxBatchSize);

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await WriteBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        if (works.Count <= 1)
        {
            return await WriteBatchAsync(works, ct).ConfigureAwait(false);
        }

        var outcomes = new DeliveryOutcome[works.Count];
        foreach (var chunk in works.Select((w, i) => (Work: w, Index: i)).Chunk(MaxBatch))
        {
            var results = await WriteBatchAsync(chunk.Select(c => c.Work).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                outcomes[chunk[i].Index] = results[i];
            }
        }

        return outcomes;
    }

    /// <summary>One request for the whole batch; a rejected batch of more than one record falls back to one request per record.</summary>
    private async Task<IReadOnlyList<DeliveryOutcome>> WriteBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct)
    {
        var outcomes = new DeliveryOutcome[works.Count];
        var toWrite = new List<(int Index, DeliveryWork Work, JsonObject Document)>();
        for (var i = 0; i < works.Count; i++)
        {
            var work = works[i];
            if (!work.DeliverMetadata)
            {
                outcomes[i] = new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion };
                continue;
            }

            try
            {
                var document = (JsonObject)work.Document.DeepClone();
                if (_options.PreserveDataKeys.Count > 0 && work.ExistingVersion is not null)
                {
                    await RecordWriter.PreserveAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, work.TargetId, document, _options.PreserveDataKeys, ct).ConfigureAwait(false);
                }

                toWrite.Add((i, work, document));
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex);
            }
        }

        if (toWrite.Count == 0)
        {
            return outcomes;
        }

        var steps = new DeliverySteps(_time);
        var started = steps.Now;
        var url = _client.Url(_options.RecordPath ?? DefaultRecordPath);
        var method = new HttpMethod((_options.RecordMethod ?? "PUT").ToUpperInvariant());
        HttpFetchResult result;
        try
        {
            result = await _client.SendJsonAsync(method, url, new JsonArray(toWrite.Select(t => (JsonNode)t.Document).ToArray()), null, ct).ConfigureAwait(false);
        }
        catch (HttpStatusException ex) when (toWrite.Count > 1 && ex.StatusCode is >= 400 and < 500 and not (401 or 408 or 425 or 429))
        {
            // The service refused the array as a whole; find out which records it refuses by sending them alone.
            foreach (var (index, work, _) in toWrite)
            {
                var alone = await WriteBatchAsync([work], ct).ConfigureAwait(false);
                outcomes[index] = alone[0];
            }

            return outcomes;
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            steps.Add(RecordsStep, started, (ex as HttpStatusException)?.StatusCode, null, ex.Message);
            foreach (var (index, _, _) in toWrite)
            {
                outcomes[index] = DeliveryOutcome.Failed(ex, steps.Steps);
            }

            return outcomes;
        }

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        string? single = null;
        if (result.Body.Length > 0)
        {
            var root = OsduHttpClient.ParseJson(result, url);
            // A single-record write also honours the flow's versionPath, for services whose response names the
            // record differently from the id that was sent.
            single = toWrite.Count == 1 ? JsonPathReader.SelectValue(root, _options.VersionPath) : null;
            foreach (var idVersion in JsonPathReader.SelectValues(root, "recordIdVersions[*]"))
            {
                var colon = idVersion.LastIndexOf(':');
                if (colon > 0)
                {
                    versions[idVersion[..colon]] = idVersion[(colon + 1)..];
                }
            }

            foreach (var id in JsonPathReader.SelectValues(root, "skippedRecordIds[*]"))
            {
                skipped.Add(id);
            }
        }

        foreach (var (index, work, _) in toWrite)
        {
            var version = versions.TryGetValue(work.TargetId, out var text) ? RecordWriter.ParseVersion(text) : RecordWriter.ParseVersion(single);
            var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
            if (version is { } v)
            {
                returned["version"] = v.ToString(CultureInfo.InvariantCulture);
            }

            if (skipped.Contains(work.TargetId))
            {
                returned["skipped"] = "true";
            }

            var own = new DeliverySteps(_time);
            own.Add(RecordsStep, started, (int)result.Status, returned);
            await work.ReportStepAsync(RecordsStep, returned, ct).ConfigureAwait(false);
            outcomes[index] = new DeliveryOutcome
            {
                MetadataDelivered = true,
                PayloadDelivered = false,
                TargetVersion = version ?? work.ExistingVersion,
                Returned = returned,
                Steps = own.Steps,
                Detail = skipped.Contains(work.TargetId) ? "unchanged at the target" : null,
            };
        }

        return outcomes;
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
    public Task<DeleteOutcome> DeleteAsync(string targetId, bool purge, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
        => RecordWriter.DeleteAsync(_client, _options.DeletePath ?? DefaultDeletePath, _options.PurgePath ?? DefaultPurgePath, targetId, purge, ct);
}

/// <summary>The record write, read-back, probe and delete shared by the OSDU protocols.</summary>
internal static class RecordWriter
{
    /// <summary>Writes one record through the array endpoint and returns the version the response reported, with the status.</summary>
    public static async Task<(long? Version, int Status)> WriteAsync(OsduHttpClient client, ProtocolOptions options, string recordPath, string method, string verifyPath, DeliveryWork work, CancellationToken ct)
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
            return (null, (int)result.Status);
        }

        var root = OsduHttpClient.ParseJson(result, url);
        var idVersion = JsonPathReader.SelectValue(root, options.VersionPath);
        return (ParseVersion(idVersion), (int)result.Status);
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

    /// <summary>The storage service delete: logical through <c>{id}:delete</c>, physical through <c>DELETE {id}</c>.</summary>
    public static async Task<DeleteOutcome> DeleteAsync(OsduHttpClient client, string deletePath, string purgePath, string targetId, bool purge, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = purge ? client.Url(purgePath, targetId) : client.Url(deletePath, targetId);
        var method = purge ? HttpMethod.Delete : HttpMethod.Post;
        var result = await client.SendJsonAsync(method, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        return (int)result.Status == 404
            ? new DeleteOutcome(false, true, "record not found in OSDU")
            : new DeleteOutcome(true, false, purge ? "purged (all versions)" : "logically deleted");
    }

    /// <summary>Copies the OSDU-owned data keys (design.md section 7.6) from the current record into the document.</summary>
    public static async Task PreserveAsync(OsduHttpClient client, string verifyPath, string targetId, JsonObject document, IReadOnlyList<string> keys, CancellationToken ct)
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
