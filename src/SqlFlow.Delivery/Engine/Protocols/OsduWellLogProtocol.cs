using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Record plus bulk (design.md sections 8.1 and 8.3): the metadata record through the wellbore DDMS record endpoint,
/// then the payload chunks streamed past the service. A single chunk goes in one request; more than the threshold
/// opens an overwrite session, streams each chunk in order and commits. Chunks are never buffered: each request
/// re-opens its blob so the retry stack can resend it, and carries its length. Each step reports what the service
/// returned (the record version, the session id, the chunk count); a retry after a payload failure resumes past
/// the metadata step it already completed.
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

    public const string MetadataStep = "metadata";
    public const string PayloadStep = "payload";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly long _requestBodyCeiling;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public OsduWellLogProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _logger = logger;
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduWellLog;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var steps = new DeliverySteps(_time);
        var version = work.ExistingVersion;
        var metadataDelivered = false;

        IReadOnlyList<Drops.PayloadChunk> chunks = [];
        if (work.DeliverPayload)
        {
            // Check the payload against the ceilings before anything is sent, so an oversized chunk holds the
            // record instead of failing mid-session after the metadata write (design.md section 14.3).
            var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
            chunks = await payload.ListChunksAsync(ct).ConfigureAwait(false);
            if (chunks.Count == 0)
            {
                throw new RecordHeldException("no payload chunk files were found for the record");
            }

            await PreflightAsync(payload, chunks, ct).ConfigureAwait(false);
        }

        if (work.DeliverMetadata)
        {
            if (work.Completed(MetadataStep) is { } done)
            {
                // The previous try wrote the record and failed later: reuse its version, do not write it again.
                steps.Resumed(MetadataStep, done);
                version = done.TryGetValue("version", out var text) ? RecordWriter.ParseVersion(text) ?? version : version;
            }
            else
            {
                // The wellbore DDMS refuses a log whose ReferenceCurveID names no curve of its own ("WellLog[0] should
                // have a curve with a curveID value equal to the ReferenceCurveID value", HTTP 400 from a live M26 service;
                // the OpenAPI description does not state the rule). Holding it here names the curves the log does
                // describe and saves a write that can only be refused.
                if (ReferenceCurveProblem(work.Document) is { } problem)
                {
                    throw new RecordHeldException(problem);
                }

                var started = steps.Now;
                var (written, status) = await RecordWriter.WriteAsync(_client, _options, _options.RecordPath ?? Ddms(DefaultRecordPath), _options.RecordMethod ?? "POST", _options.VerifyPath ?? Ddms(DefaultVerifyPath), work, ct).ConfigureAwait(false);
                version = written ?? version;
                var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
                if (version is { } v)
                {
                    returned["version"] = v.ToString(CultureInfo.InvariantCulture);
                }

                steps.Add(MetadataStep, started, status, returned);
                await work.ReportStepAsync(MetadataStep, returned, ct).ConfigureAwait(false);
            }

            metadataDelivered = true;
        }

        var chunksSent = 0;
        var payloadDelivered = false;
        if (work.DeliverPayload)
        {
            var payload = work.Payload!;
            var started = steps.Now;
            string? sessionId = null;

            // One chunk may go straight to the bulk endpoint, and only one: that request carries "the entire bulk
            // which will replace as latest version any previous bulk" (openapi wellbore_ddms,
            // POST /ddms/v3/welllogs/{record_id}/data). Posting several chunks to it in turn would leave the record
            // holding the last one and report every one of them as delivered. Aggregating chunks is what a session
            // is for, so anything past the first uses one.
            if (chunks.Count == 1 && _options.SessionThresholdChunks >= 1)
            {
                var chunk = chunks[0];
                var url = _client.Url(_options.DataPath ?? Ddms(DefaultDataPath), work.TargetId);
                await _client.SendStreamAsync(HttpMethod.Post, url, () => OpenSync(payload, chunk), _options.PayloadContentType, chunk.Size > 0 ? chunk.Size : null, ct, idempotent: true).ConfigureAwait(false);
                chunksSent = 1;
            }
            else
            {
                (chunksSent, sessionId) = await SendSessionAsync(work, payload, chunks, version, ct).ConfigureAwait(false);
            }

            var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["chunks"] = chunksSent.ToString(CultureInfo.InvariantCulture) };
            if (sessionId is not null)
            {
                returned["sessionId"] = sessionId;
            }

            // Writing the bulk creates a new version of the record ("It creates a new version", openapi wellbore_ddms,
            // POST /ddms/v3/welllogs/{record_id}/data; a session commit creates one too), and the direct write documents
            // no response body to read it from. The ledger has to hold the version OSDU now serves: the metadata write's
            // version makes every verify report the record drifted, and opens the next session from a version that is
            // no longer the latest. So the record is read back.
            var verifyPath = _options.VerifyPath ?? Ddms(DefaultVerifyPath);
            var landed = await RecordWriter.VerifyAsync(_client, verifyPath, work.TargetId, null, ct).ConfigureAwait(false);
            var landedVersion = landed.ObservedVersion
                ?? throw new DeliveryException(
                    $"The bulk data for {work.TargetId} was written, but the record's version could not be read back from {verifyPath}: {landed.Detail}.");
            version = landedVersion;
            returned["version"] = landedVersion.ToString(CultureInfo.InvariantCulture);

            steps.Add(PayloadStep, started, null, returned);
            payloadDelivered = true;
        }

        var all = new Dictionary<string, string>(steps.Returned, StringComparer.Ordinal);
        all["recordId"] = work.TargetId;
        if (version is { } finalVersion)
        {
            all["version"] = finalVersion.ToString(CultureInfo.InvariantCulture);
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = metadataDelivered,
            PayloadDelivered = payloadDelivered,
            TargetVersion = version,
            ChunksSent = chunksSent,
            Detail = chunksSent > 0 ? $"{chunksSent} chunk(s)" : null,
            Returned = all,
            Steps = steps.Steps,
        };
    }

    /// <summary>
    /// Why a log's <c>data.ReferenceCurveID</c> names no curve among its <c>data.Curves</c>, or null when it names one of
    /// them or names none at all. Curve ids are compared exactly, as the service compares them.
    /// </summary>
    internal static string? ReferenceCurveProblem(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document["data"] is not JsonObject data
            || data["ReferenceCurveID"] is not JsonValue reference
            || !reference.TryGetValue<string>(out var id)
            || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var curves = (data["Curves"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(curve => curve["CurveID"] is JsonValue value && value.TryGetValue<string>(out var curveId) ? curveId : null)
            .OfType<string>()
            .ToList();
        if (curves.Contains(id, StringComparer.Ordinal))
        {
            return null;
        }

        var described = curves.Count == 0 ? "describes no curve" : "describes only " + string.Join(", ", curves);
        return $"data.ReferenceCurveID is '{id}' but data.Curves {described}; the wellbore DDMS refuses a log whose reference curve is not one of its curves";
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? Ddms(DefaultVerifyPath), targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? Ddms(DefaultVerifyPath), targetId, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? Ddms(DefaultProbePath), ct);

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

    /// <summary>
    /// Wellbore DDMS semantics (openapi wellbore_ddms, DELETE /ddms/v3/welllogs/{record_id}): a logical deletion of
    /// the record by default, a physical one with <c>?purge=true</c>; no recursive delete of owned entities; 204.
    ///
    /// The DDMS has no operation on a record's versions (its only versions route is a GET listing), and versions
    /// belong to the storage service for every kind of record, so <see cref="RemovalScope.History"/> goes to
    /// storage's version purge and leaves the DDMS record itself untouched, which is exactly what that scope
    /// promises. Storage is a different service from this flow's endpoint, though, and the DDMS paths carry no
    /// <c>/api/&lt;service&gt;/</c> prefix, so the storage default cannot be resolved under a DDMS endpoint. The
    /// flow says where storage lives by declaring <c>protocolOptions.purgeVersionsPath</c>, usually as an absolute
    /// URL. Without it the scope is refused, because guessing would send a delete somewhere nobody chose.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (scope == RemovalScope.History)
        {
            if (HistoryPath(_options) is not { } purgeVersions)
            {
                throw new RecordHeldException(
                    "the wellbore DDMS has no version purge, and this flow does not say where the storage service is. "
                    + "Declare target.protocolOptions.ddmsRoot when the endpoint is the OSDU platform root, or "
                    + "purgeVersionsPath (an absolute URL such as https://<host>/api/storage/v2/records/{id}/versions) "
                    + "when it is the DDMS itself, before purging a well log's history.");
            }

            return await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options) with { PurgeVersions = purgeVersions }, targetId, scope, ct).ConfigureAwait(false);
        }

        var url = _client.Url(_options.DeletePath ?? Ddms(DefaultDeletePath), targetId);
        if (scope == RemovalScope.Everything)
        {
            url = new Uri(url + (string.IsNullOrEmpty(url.Query) ? "?purge=true" : "&purge=true"));
        }

        var result = await _client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return new DeleteOutcome(false, true, "record not found in OSDU");
        }

        return new DeleteOutcome(true, false, scope == RemovalScope.Everything
            ? "purged from OSDU (the record and every version)"
            : "removed from OSDU (reversible)");
    }

    /// <summary>
    /// Two ceilings bound a chunk, and both are checked before the first request. The estate's request body size is
    /// declared as <c>reliability.maxRequestBodyBytes</c> and can be raised where it is configured. The wellbore
    /// DDMS bulk shape (<see cref="WellboreDdmsBulkLimits"/>) cannot: it is the frame the service materialises, so
    /// a chunk can be small enough to send and still be too large to accept. The shape is read from the parquet
    /// footer, never from the chunk's contents, and only when the payload is parquet and a ceiling is in force.
    /// </summary>
    private async Task PreflightAsync(IPayloadSource payload, IReadOnlyList<Drops.PayloadChunk> chunks, CancellationToken ct)
    {
        var checksShape = (_options.MaxChunkValues > 0 || _options.MaxChunkColumns > 0)
            && _options.PayloadContentType.Contains("parquet", StringComparison.OrdinalIgnoreCase);

        foreach (var chunk in chunks)
        {
            if (_requestBodyCeiling > 0 && chunk.Size > _requestBodyCeiling)
            {
                throw new RecordHeldException(
                    $"payload chunk {chunk.Index.ToString(CultureInfo.InvariantCulture)} is {chunk.Size.ToString(CultureInfo.InvariantCulture)} bytes, above the target's declared request body ceiling of {_requestBodyCeiling.ToString(CultureInfo.InvariantCulture)} bytes (reliability.maxRequestBodyBytes); re-chunk in prepare");
            }

            if (!checksShape)
            {
                continue;
            }

            var shape = await ShapeAsync(payload, chunk, ct).ConfigureAwait(false);
            if (WellboreDdmsBulkLimits.Exceeded(chunk.Index, chunk.Path, shape.Rows, shape.Columns, _options.MaxChunkValues, _options.MaxChunkColumns) is { } held)
            {
                throw new RecordHeldException(held);
            }
        }
    }

    /// <summary>Reads one chunk's shape from its footer. A chunk that does not parse holds the record: the service would refuse it too.</summary>
    private static async Task<ParquetShape> ShapeAsync(IPayloadSource payload, Drops.PayloadChunk chunk, CancellationToken ct)
    {
        var opened = await payload.OpenAsync(chunk, ct).ConfigureAwait(false);
        Stream seekable;
        try
        {
            seekable = await ParquetScopeReader.EnsureSeekableAsync(opened, ct).ConfigureAwait(false);
        }
        catch
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await using (seekable.ConfigureAwait(false))
        {
            try
            {
                return await ParquetScopeReader.ReadShapeAsync(seekable, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RecordHeldException(
                    $"payload chunk {chunk.Index.ToString(CultureInfo.InvariantCulture)} ({Path.GetFileName(chunk.Path)}) is declared as parquet but its footer could not be read, so its shape cannot be checked against the target's bulk ceilings: {HeaderRedaction.RedactMessage(ex.Message)}",
                    ex);
            }
        }
    }

    private async Task<(int Sent, string SessionId)> SendSessionAsync(DeliveryWork work, IPayloadSource payload, IReadOnlyList<Drops.PayloadChunk> chunks, long? version, CancellationToken ct)
    {
        var createUrl = _client.Url(_options.SessionPath ?? Ddms(DefaultSessionPath), work.TargetId);
        var createBody = new JsonObject
        {
            ["mode"] = "overwrite",
            ["fromVersion"] = version ?? 0,
            ["timeToLive"] = 1440,
        };
        var created = await _client.SendJsonAsync(HttpMethod.Post, createUrl, createBody, null, ct, idempotent: false).ConfigureAwait(false);
        var sessionId = JsonPathReader.SelectValue(OsduHttpClient.ParseJson(created, createUrl), "id")
            ?? throw new DeliveryException($"{createUrl} did not return a session id.");

        var sent = 0;
        try
        {
            foreach (var chunk in chunks)
            {
                var url = _client.Url(_options.SessionDataPath ?? Ddms(DefaultSessionDataPath), work.TargetId, sessionId);

                // A chunk sent twice into a session lands twice in the committed bulk, so a chunk whose outcome is
                // unclear fails the session (which is abandoned) rather than being resent.
                await _client.SendStreamAsync(HttpMethod.Post, url, () => OpenSync(payload, chunk), _options.PayloadContentType, chunk.Size > 0 ? chunk.Size : null, ct, idempotent: false).ConfigureAwait(false);
                sent++;
            }

            await CommitAsync(work.TargetId, sessionId, ct).ConfigureAwait(false);
            return (sent, sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AbandonAsync(work.TargetId, sessionId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Commits the session, and settles a commit the service answers 409 or 412 to by asking what state the session
    /// is actually in (openapi wellbore_ddms, GET /ddms/v3/welllogs/{record_id}/sessions/{session_id}).
    ///
    /// The commit is a PATCH and is never resent blind, so its outcome can be unclear: the connection went, a
    /// gateway answered 5xx after the service had acted, or an intermediary resent it and the second copy met a
    /// session that is no longer open (409 or 412). Treating any of those as a failure would fail a record whose
    /// bulk data did land, and send the whole payload again on the next try. The session's own state says which
    /// happened, so it is read rather than guessed: committed or committing is the commit that worked, anything
    /// else is a real failure.
    /// </summary>
    private async Task CommitAsync(string targetId, string sessionId, CancellationToken ct)
    {
        var commitUrl = _client.Url(_options.SessionCommitPath ?? Ddms(DefaultSessionCommitPath), targetId, sessionId);
        try
        {
            await _client.SendJsonAsync(HttpMethod.Patch, commitUrl, new JsonObject { ["state"] = "commit" }, null, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is HttpStatusException { StatusCode: 409 or 412 or >= 500 } || ex is DeliveryException { InnerException: HttpRequestException or IOException or TaskCanceledException })
        {
            var state = await SessionStateAsync(targetId, sessionId, ct).ConfigureAwait(false);
            if (state is not ("committed" or "committing"))
            {
                throw new DeliveryException(
                    $"session {sessionId} for {targetId} could not be committed and is {state ?? "in an unknown state"}; the payload did not land.", ex);
            }

            _logger.LogInformation(
                "Session {SessionId} for {TargetId} was already {State} when the commit was resent; the payload landed on the first commit.",
                sessionId, targetId, state);
        }
    }

    /// <summary>The session's state as the service reports it, or null when it cannot be read.</summary>
    private async Task<string?> SessionStateAsync(string targetId, string sessionId, CancellationToken ct)
    {
        try
        {
            var url = _client.Url(_options.SessionCommitPath ?? Ddms(DefaultSessionCommitPath), targetId, sessionId);
            var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
            if ((int)result.Status == 404 || result.Body.Length == 0)
            {
                return null;
            }

            return JsonPathReader.SelectValue(OsduHttpClient.ParseJson(result, url), "state")?.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            _logger.LogWarning("Could not read session {SessionId} for {TargetId}: {Message}", sessionId, targetId, HeaderRedaction.RedactMessage(ex.Message));
            return null;
        }
    }

    private async Task AbandonAsync(string targetId, string sessionId)
    {
        try
        {
            var url = _client.Url(_options.SessionCommitPath ?? Ddms(DefaultSessionCommitPath), targetId, sessionId);
            await _client.SendJsonAsync(HttpMethod.Patch, url, new JsonObject { ["state"] = "abandon" }, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            _logger.LogWarning("Could not abandon session {SessionId} for {TargetId}: {Message}", sessionId, targetId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>A DDMS default path, under <see cref="ProtocolOptions.DdmsRoot"/> when the endpoint is the platform root.</summary>
    private string Ddms(string path) => DdmsPath(_options, path);

    /// <summary>A DDMS default path as a flow resolves it: under its DDMS root when it declares one, else as written.</summary>
    internal static string DdmsPath(ProtocolOptions options, string path)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.DdmsRoot is { Length: > 0 } root ? root.TrimEnd('/') + path : path;
    }

    /// <summary>
    /// Where a well log flow sends the storage history purge: its explicit path, or the storage default when the
    /// endpoint is the platform root (a DDMS root is declared), or nowhere, when the endpoint is the DDMS itself and
    /// storage is somewhere this flow has not named.
    /// </summary>
    internal static string? HistoryPath(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PurgeVersionsPath is { Length: > 0 } explicitPath)
        {
            return explicitPath;
        }

        return options.DdmsRoot is { Length: > 0 } ? OsduRecordProtocol.DefaultPurgeVersionsPath : null;
    }

    /// <summary>The request factory is synchronous; opening a blob stream is cheap and the copy is what streams.</summary>
    internal static Stream OpenSync(IPayloadSource payload, Drops.PayloadChunk chunk)
        => payload.OpenAsync(chunk).GetAwaiter().GetResult();
}
