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
    public const string DefaultPurgeVersionsPath = "/api/storage/v2/records/{id}/versions";
    public const string DefaultBulkDeletePath = "/api/storage/v2/records/delete";
    public const string DefaultVerifyBatchPath = "/api/storage/v2/query/records";

    /// <summary>Record ids the storage service accepts in one bulk soft delete request.</summary>
    public const int MaxBulkDelete = 500;

    /// <summary>Record ids one batched read takes (openapi storage v2, MultiRecordIds caps the list at 100).</summary>
    public const int MaxVerifyBatch = 100;

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

    int IDeliveryProtocol.MaxVerifyBatch => MaxVerifyBatch;

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
        if (_options.SkipDuplicates)
        {
            url = OsduHttpClient.WithQuery(url, "skipdupes", "true");
        }

        var method = new HttpMethod((_options.RecordMethod ?? "PUT").ToUpperInvariant());
        HttpFetchResult result;
        try
        {
            result = await _client.SendJsonAsync(method, url, new JsonArray(toWrite.Select(t => (JsonNode)t.Document).ToArray()), null, ct, idempotent: true).ConfigureAwait(false);
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

    public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
        => RecordWriter.VerifyBatchAsync(_client, _options.VerifyBatchPath ?? DefaultVerifyBatchPath, requests, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? DefaultProbePath, ct);

    private LegalTagValidator? _legal;

    /// <summary>Asks the legal service under this target, when the flow's target reaches it (see <see cref="LegalTagValidator.PathFor"/>).</summary>
    public async Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (LegalTagValidator.PathFor(Kind, _options) is not { } path)
        {
            return null;
        }

        _legal ??= new LegalTagValidator(_client, path, _time);
        return await _legal.InvalidAsync(tags, ct).ConfigureAwait(false);
    }

    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
        => RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), targetId, scope, ct);

    /// <summary>
    /// The reversible scope goes through the storage service's bulk soft delete (openapi storage v2,
    /// <c>POST /records/delete</c>), which takes up to <see cref="MaxBulkDelete"/> ids per request and answers 204
    /// when it deleted them all or 207 when it did not. A 207, and a status that rejects the whole request, fall
    /// back to removing that chunk one record at a time, so every record still reports its own outcome instead of
    /// sharing a guess. The two purges have no bulk endpoint and always go one at a time.
    /// </summary>
    public async Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        if (scope != RemovalScope.Record || removals.Count <= 1)
        {
            return await OneByOneAsync(removals, scope, ct).ConfigureAwait(false);
        }

        var results = new List<RemovalResult>(removals.Count);
        foreach (var chunk in removals.Chunk(MaxBulkDelete))
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(await BulkSoftDeleteAsync(chunk, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<IReadOnlyList<RemovalResult>> BulkSoftDeleteAsync(IReadOnlyList<RecordRemoval> chunk, CancellationToken ct)
    {
        var url = _client.Url(_options.BulkDeletePath ?? DefaultBulkDeletePath);
        var body = new JsonArray(chunk.Select(r => (JsonNode?)JsonValue.Create(r.TargetId)).ToArray());
        HttpFetchResult result;
        try
        {
            result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 207, 404 }, ct, idempotent: true).ConfigureAwait(false);
        }
        catch (HttpStatusException ex) when (ex.StatusCode is 400 or 405)
        {
            // The service refused the list over something in it (a malformed id) or does not offer the bulk
            // endpoint at all; ask it record by record which ones it objects to.
            return await OneByOneAsync(chunk, RemovalScope.Record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            // Not a verdict on the records: the caller is unauthorised, throttled past its retries, or the service
            // is down. Sending the chunk again one id at a time would repeat the same failure hundreds of times, so
            // every record in it carries the one failure that actually happened.
            return chunk.Select(r => new RemovalResult(r, null, ex)).ToList();
        }

        if ((int)result.Status is 207 or 404)
        {
            return await OneByOneAsync(chunk, RemovalScope.Record, ct).ConfigureAwait(false);
        }

        return chunk
            .Select(r => new RemovalResult(r, new DeleteOutcome(true, false, "removed from OSDU (reversible, in bulk)"), null))
            .ToList();
    }

    /// <summary>The shared per-record removal, which is what every fallback here lands on.</summary>
    private Task<IReadOnlyList<RemovalResult>> OneByOneAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct)
        => ((IDeliveryProtocol)this).DeleteOneByOneAsync(removals, scope, ct);
}

/// <summary>The three removal endpoints of one flow's target, defaulted per protocol and overridable per flow.</summary>
internal sealed record RemovalPaths(string Delete, string PurgeVersions, string Purge)
{
    public static RemovalPaths From(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RemovalPaths(
            options.DeletePath ?? OsduRecordProtocol.DefaultDeletePath,
            options.PurgeVersionsPath ?? OsduRecordProtocol.DefaultPurgeVersionsPath,
            options.PurgePath ?? OsduRecordProtocol.DefaultPurgePath);
    }

    public string For(RemovalScope scope) => scope switch
    {
        RemovalScope.Record => Delete,
        RemovalScope.History => PurgeVersions,
        RemovalScope.Everything => Purge,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
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
        var result = await client.SendJsonAsync(new HttpMethod(method.ToUpperInvariant()), url, body, null, ct, idempotent: true).ConfigureAwait(false);
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

    /// <summary>
    /// Verifies a set of records in batched reads (openapi storage v2, <c>POST /query/records</c>, which takes up
    /// to <see cref="OsduRecordProtocol.MaxVerifyBatch"/> ids per request), rather than one read per record.
    /// Attributes are projected down so the service does not return whole data blocks for a pass that only compares
    /// versions. The records the response returns carry their observed version; a record it does not return is missing
    /// from the target, whether or not it names the id under <c>invalidRecords</c>, which is how storage answers for a
    /// record it does not hold (observed on a live M26 service; the OpenAPI description does not say what the list means).
    ///
    /// Every protocol that writes through the storage service verifies through this, which is what keeps a drift
    /// pass over a large estate to a handful of requests whichever of them delivered the records.
    /// </summary>
    public static async Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(OsduHttpClient client, string batchPath, IReadOnlyList<VerifyRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var results = new VerifyResult[requests.Count];
        var url = client.Url(batchPath);
        foreach (var chunk in requests.Select((r, i) => (Request: r, Index: i)).Chunk(OsduRecordProtocol.MaxVerifyBatch))
        {
            ct.ThrowIfCancellationRequested();
            var body = new JsonObject
            {
                ["records"] = new JsonArray(chunk.Select(c => (JsonNode?)JsonValue.Create(c.Request.TargetId)).ToArray()),

                // A projection the service understands and that matches no data field, so the read carries record
                // headers (id and version among them) and not every record's payload.
                ["attributes"] = new JsonArray(JsonValue.Create("id")),
            };
            var result = await client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
            var versions = new Dictionary<string, long?>(StringComparer.Ordinal);
            var invalid = new HashSet<string>(StringComparer.Ordinal);
            if (result.Body.Length > 0)
            {
                var root = OsduHttpClient.ParseJson(result, url);
                foreach (var record in JsonPathReader.SelectElements(root, "records[*]"))
                {
                    if (record.ValueKind == JsonValueKind.Object
                        && record.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && id.GetString() is { } text)
                    {
                        versions[text] = ParseVersion(JsonPathReader.SelectValue(record, "version"));
                    }
                }

                foreach (var id in JsonPathReader.SelectValues(root, "invalidRecords[*]"))
                {
                    invalid.Add(id);
                }
            }

            foreach (var (request, index) in chunk)
            {
                results[index] = SettleVerify(request, versions, invalid);
            }
        }

        return results;
    }

    /// <summary>What one record's batched read means for it: its version, or an absence.</summary>
    private static VerifyResult SettleVerify(VerifyRequest request, IReadOnlyDictionary<string, long?> versions, IReadOnlySet<string> invalid)
    {
        if (!versions.TryGetValue(request.TargetId, out var observed))
        {
            return new VerifyResult(VerifyOutcome.Missing, null, invalid.Contains(request.TargetId)
                ? "record not found (storage names the id under invalidRecords, as it does for a record it does not hold)"
                : "record not found");
        }

        if (observed is null)
        {
            return new VerifyResult(VerifyOutcome.Error, null, "record has no version");
        }

        if (request.ExpectedVersion is null)
        {
            return new VerifyResult(VerifyOutcome.Match, observed, "no expected version recorded; observed version adopted");
        }

        return observed == request.ExpectedVersion
            ? new VerifyResult(VerifyOutcome.Match, observed, null)
            : new VerifyResult(VerifyOutcome.Drifted, observed, $"observed version {observed.Value.ToString(CultureInfo.InvariantCulture)}, ledger holds {request.ExpectedVersion.Value.ToString(CultureInfo.InvariantCulture)}");
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

    /// <summary>
    /// The storage service's three removals (openapi storage v2), each a different endpoint and a different
    /// promise: <c>POST /records/{id}:delete</c> stops the record resolving and OSDU can revert it;
    /// <c>DELETE /records/{id}/versions</c> destroys every earlier version and leaves the latest live;
    /// <c>DELETE /records/{id}</c> destroys the record and all of its versions. All three answer 204, and a record
    /// that is already gone (404) is reported as such rather than as a failure.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteAsync(OsduHttpClient client, RemovalPaths paths, string targetId, RemovalScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = client.Url(paths.For(scope), targetId);
        var method = scope == RemovalScope.Record ? HttpMethod.Post : HttpMethod.Delete;
        var result = await client.SendJsonAsync(method, url, null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return new DeleteOutcome(false, true, "record not found in OSDU");
        }

        return new DeleteOutcome(true, false, scope switch
        {
            RemovalScope.Record => "removed from OSDU (reversible)",
            RemovalScope.History => "earlier versions purged; the latest version is still live",
            RemovalScope.Everything => "purged from OSDU (the record and every version)",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        });
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
