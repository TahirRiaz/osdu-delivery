using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Record plus bulk (design.md sections 8.1 and 8.3): the metadata record through the wellbore DDMS record endpoint,
/// then the payload chunks streamed past the service. A single chunk goes in one request; more than the threshold
/// opens an overwrite session, streams each chunk in order and commits. Chunks are never buffered: each request
/// re-opens its blob so the retry stack can resend it.
/// </summary>
public sealed class OsduWellLogProtocol : IDeliveryProtocol
{
    public const string DefaultRecordPath = "/ddms/v3/welllogs";
    public const string DefaultDataPath = "/ddms/v3/welllogs/{id}/data";
    public const string DefaultSessionPath = "/ddms/v3/welllogs/{id}/sessions";
    public const string DefaultSessionDataPath = "/ddms/v3/welllogs/{id}/sessions/{sessionId}/data";
    public const string DefaultSessionCommitPath = "/ddms/v3/welllogs/{id}/sessions/{sessionId}";
    public const string DefaultVerifyPath = "/ddms/v3/welllogs/{id}";
    public const string DefaultDeletePath = "/ddms/v3/welllogs/{id}";
    public const string DefaultProbePath = "/about";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly long _requestBodyCeiling;
    private readonly ILogger _logger;

    public OsduWellLogProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _logger = logger;
        _requestBodyCeiling = requestBodyCeiling;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduWellLog;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var version = work.ExistingVersion;
        var metadataDelivered = false;

        IReadOnlyList<Drops.PayloadChunk> chunks = [];
        if (work.DeliverPayload)
        {
            // Check the payload against the declared ceiling before anything is sent, so an oversized chunk holds
            // the record instead of failing mid-session after the metadata write (design.md section 14.3).
            var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
            chunks = await payload.ListChunksAsync(ct).ConfigureAwait(false);
            if (chunks.Count == 0)
            {
                throw new RecordHeldException("no payload chunk files were found for the record");
            }

            if (_requestBodyCeiling > 0)
            {
                foreach (var chunk in chunks)
                {
                    if (chunk.Size > _requestBodyCeiling)
                    {
                        throw new RecordHeldException(
                            $"payload chunk {chunk.Index} is {chunk.Size.ToString(CultureInfo.InvariantCulture)} bytes, above the target's declared request body ceiling of {_requestBodyCeiling.ToString(CultureInfo.InvariantCulture)} bytes (reliability.maxRequestBodyBytes); re-chunk in prepare");
                    }
                }
            }
        }

        if (work.DeliverMetadata)
        {
            version = await RecordWriter.WriteAsync(_client, _options, _options.RecordPath ?? DefaultRecordPath, _options.RecordMethod ?? "POST", _options.VerifyPath ?? DefaultVerifyPath, work, ct).ConfigureAwait(false) ?? version;
            metadataDelivered = true;
        }

        var chunksSent = 0;
        var payloadDelivered = false;
        if (work.DeliverPayload)
        {
            var payload = work.Payload!;
            if (chunks.Count <= Math.Max(1, _options.SessionThresholdChunks))
            {
                foreach (var chunk in chunks)
                {
                    var url = _client.Url(_options.DataPath ?? DefaultDataPath, work.TargetId);
                    await _client.SendStreamAsync(HttpMethod.Post, url, () => OpenSync(payload, chunk), _options.PayloadContentType, ct).ConfigureAwait(false);
                    chunksSent++;
                }
            }
            else
            {
                chunksSent = await SendSessionAsync(work, payload, chunks, version, ct).ConfigureAwait(false);
            }

            payloadDelivered = true;
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = metadataDelivered,
            PayloadDelivered = payloadDelivered,
            TargetVersion = version,
            ChunksSent = chunksSent,
            Detail = chunksSent > 0 ? $"{chunksSent} chunk(s)" : null,
        };
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? DefaultProbePath, ct);

    /// <summary>
    /// Wellbore DDMS semantics (openapi wellbore_ddms, DELETE /ddms/v3/welllogs/{record_id}): a logical deletion of
    /// the record by default, a physical one with <c>?purge=true</c>; no recursive delete of owned entities; 204.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, bool purge, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = _client.Url(_options.DeletePath ?? DefaultDeletePath, targetId);
        if (purge)
        {
            url = new Uri(url + (string.IsNullOrEmpty(url.Query) ? "?purge=true" : "&purge=true"));
        }

        var result = await _client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        return (int)result.Status == 404
            ? new DeleteOutcome(false, true, "record not found in OSDU")
            : new DeleteOutcome(true, false, purge ? "purged" : "logically deleted");
    }

    private async Task<int> SendSessionAsync(DeliveryWork work, IPayloadSource payload, IReadOnlyList<Drops.PayloadChunk> chunks, long? version, CancellationToken ct)
    {
        var createUrl = _client.Url(_options.SessionPath ?? DefaultSessionPath, work.TargetId);
        var createBody = new JsonObject
        {
            ["mode"] = "overwrite",
            ["fromVersion"] = version ?? 0,
            ["timeToLive"] = 1440,
        };
        var created = await _client.SendJsonAsync(HttpMethod.Post, createUrl, createBody, null, ct).ConfigureAwait(false);
        var sessionId = JsonPathReader.SelectValue(OsduHttpClient.ParseJson(created, createUrl), "id")
            ?? throw new DeliveryException($"{createUrl} did not return a session id.");

        var sent = 0;
        try
        {
            foreach (var chunk in chunks)
            {
                var url = _client.Url(_options.SessionDataPath ?? DefaultSessionDataPath, work.TargetId, sessionId);
                await _client.SendStreamAsync(HttpMethod.Post, url, () => OpenSync(payload, chunk), _options.PayloadContentType, ct).ConfigureAwait(false);
                sent++;
            }

            var commitUrl = _client.Url(_options.SessionCommitPath ?? DefaultSessionCommitPath, work.TargetId, sessionId);
            await _client.SendJsonAsync(HttpMethod.Patch, commitUrl, new JsonObject { ["state"] = "commit" }, null, ct).ConfigureAwait(false);
            return sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AbandonAsync(work.TargetId, sessionId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task AbandonAsync(string targetId, string sessionId)
    {
        try
        {
            var url = _client.Url(_options.SessionCommitPath ?? DefaultSessionCommitPath, targetId, sessionId);
            await _client.SendJsonAsync(HttpMethod.Patch, url, new JsonObject { ["state"] = "abandon" }, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException)
        {
            _logger.LogWarning("Could not abandon session {SessionId} for {TargetId}: {Message}", sessionId, targetId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>The request factory is synchronous; opening a blob stream is cheap and the copy is what streams.</summary>
    private static Stream OpenSync(IPayloadSource payload, Drops.PayloadChunk chunk)
        => payload.OpenAsync(chunk).GetAwaiter().GetResult();
}
