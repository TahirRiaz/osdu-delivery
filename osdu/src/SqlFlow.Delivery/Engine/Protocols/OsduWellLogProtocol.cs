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
/// The ddms route (design.md sections 8.1 and 8.3, docs/interfaces-design.md section 5.4): each record through the
/// collection of the DDMS serving its entity type (<see cref="DdmsRouting"/>), then, on a bulk collection, its bulk data
/// streamed past the service. A single chunk goes in one request; more than the threshold opens an overwrite session,
/// streams each chunk in order and commits. Chunks are never buffered: each request re-opens its blob so the retry stack
/// can resend it, and carries its length. Each step reports what the service returned (the record version, the session
/// id, the chunk count); a retry after a payload failure resumes past the metadata step it already completed.
/// Every rule the Wellbore DDMS applies that can be checked from the record and its chunks' footers is checked before
/// the first request (<see cref="WellboreDdmsRules"/>), and a metadata update carries the bulk link the DDMS manages
/// (<see cref="WellboreDdmsBulkLink"/>). The protocol is named after the <c>osduWellLog</c> value a flow's
/// <c>target.protocol</c> gives it.
/// </summary>
public sealed class OsduWellLogProtocol : IDeliveryProtocol
{
    public const string MetadataStep = "metadata";
    public const string PayloadStep = "payload";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly DdmsRouting _routing;
    private readonly long _requestBodyCeiling;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public OsduWellLogProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null, DdmsRouting? routing = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _routing = routing ?? DdmsRouting.Of(options);
        _logger = logger;
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduWellLog;

    /// <summary>Where the protocol sends each record: the flow's DDMSs, every registration among them read.</summary>
    public DdmsRouting Routing => _routing;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var paths = Held(work.TargetId);
        var steps = new DeliverySteps(_time);
        var version = work.ExistingVersion;
        var metadataDelivered = false;
        var writesMetadata = work.DeliverMetadata && work.Completed(MetadataStep) is null;

        if (work.DeliverPayload && BulkProblem(work.TargetId, paths) is { } nowhere)
        {
            throw new RecordHeldException(nowhere);
        }

        // The record rules first: they cost nothing, and a record the DDMS would refuse is held before its chunks are read.
        if ((writesMetadata || work.DeliverPayload) && WellboreDdmsRules.RecordProblem(paths.EntityType, work.Document, work.DeliverPayload) is { } problem)
        {
            throw new RecordHeldException(problem);
        }

        IReadOnlyList<PayloadFile> chunks = [];
        IReadOnlyList<ParquetShape>? shapes = null;
        var session = false;
        if (work.DeliverPayload)
        {
            // Check the payload against the ceilings, its columns against the record, and a session's chunks against each
            // other, before anything is sent, so a chunk the target cannot take whole holds the record instead of failing
            // or losing rows mid-session after the metadata write (design.md section 14.3).
            var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
            chunks = await payload.ListChunksAsync(ct).ConfigureAwait(false);
            if (chunks.Count == 0)
            {
                throw new RecordHeldException("no payload chunk files were found for the record");
            }

            session = chunks.Count > 1 || _options.SessionThresholdChunks < 1;
            shapes = await PreflightAsync(work.Document, paths, payload, chunks, session, ct).ConfigureAwait(false);
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
                var started = steps.Now;
                var (written, status) = await WriteRecordAsync(work, paths, ct).ConfigureAwait(false);
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
            // POST /ddms/v3/{collection}/{record_id}/data). Posting several chunks to it in turn would leave the record
            // holding the last one and report every one of them as delivered. Aggregating chunks is what a session
            // is for, so anything past the first uses one.
            if (!session)
            {
                var chunk = chunks[0];
                var url = _client.Url(paths.Data!, work.TargetId);
                await _client.SendStreamAsync(HttpMethod.Post, url, () => OpenSync(payload, chunk), _options.PayloadContentType, chunk.Size > 0 ? chunk.Size : null, ct, idempotent: true).ConfigureAwait(false);
                chunksSent = 1;
            }
            else
            {
                (chunksSent, sessionId) = await SendSessionAsync(work, paths, payload, chunks, version, ct).ConfigureAwait(false);
            }

            var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["chunks"] = chunksSent.ToString(CultureInfo.InvariantCulture) };
            if (sessionId is not null)
            {
                returned["sessionId"] = sessionId;

                // The labels read before the session opened keep its chunks from colliding where the footers can tell
                // them; the committed bulk is read back as well, because a collision the footers cannot show loses rows
                // just as silently.
                if (shapes is not null && await CommittedRowsAsync(work.TargetId, paths, sessionId, shapes, ct).ConfigureAwait(false) is { } rows)
                {
                    returned["rows"] = rows.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Writing the bulk creates a new version of the record ("It creates a new version", openapi wellbore_ddms,
            // POST /ddms/v3/{collection}/{record_id}/data; a session commit creates one too), and the direct write
            // documents no response body to read it from. The ledger has to hold the version OSDU now serves: the
            // metadata write's version makes every verify report the record drifted, and opens the next session from a
            // version that is no longer the latest. So the record is read back.
            var landed = await RecordWriter.VerifyAsync(_client, paths.Record, work.TargetId, null, ct).ConfigureAwait(false);
            var landedVersion = landed.ObservedVersion
                ?? throw new DeliveryException(
                    $"The bulk data for {work.TargetId} was written, but the record's version could not be read back from {paths.Record}: {landed.Detail}.");
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

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _routing.ForRecord(targetId).Record, targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _routing.ForRecord(targetId).Record, targetId, ct);

    /// <summary>
    /// Asks each DDMS the flow reaches for its service description (<c>GET /about</c>, openapi wellbore_ddms), or the
    /// flow's own probe path. The target is reachable when every one of them answers.
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        var probes = _routing.ProbePaths;
        if (probes.Count == 0)
        {
            return new ProbeOutcome(false, 0, "the flow names its own DDMS paths and reaches no DDMS whose service description could be asked; name protocolOptions.probePath", string.Empty);
        }

        var answered = new List<ProbeOutcome>(probes.Count);
        foreach (var probe in probes)
        {
            var outcome = await RecordWriter.ProbeAsync(_client, probe, ct).ConfigureAwait(false);
            if (!outcome.Reachable)
            {
                return outcome;
            }

            answered.Add(outcome);
        }

        return answered.Count == 1
            ? answered[0]
            : answered[^1] with
            {
                Detail = string.Create(CultureInfo.InvariantCulture, $"all {answered.Count} DDMSs answered"),
                Path = string.Join(", ", answered.Select(a => a.Path)),
            };
    }

    private LegalTagValidator? _legal;

    /// <summary>Asks the legal service under this target, when the flow's target reaches it (<see cref="DdmsRouting.LegalValidatePath"/>).</summary>
    public async Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (_routing.LegalValidatePath is not { } path)
        {
            return null;
        }

        _legal ??= new LegalTagValidator(_client, path, _time);
        return await _legal.InvalidAsync(tags, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wellbore DDMS semantics (openapi wellbore_ddms, DELETE /ddms/v3/{collection}/{record_id}): a logical deletion of
    /// the record by default, a physical one with <c>?purge=true</c> on the four bulk collections, which also removes
    /// the bulk data; no recursive delete of owned entities; 204. A record-only collection's DELETE is logical only, so
    /// <see cref="RemovalScope.Everything"/> goes to the storage service's purge for its records, which is the purge the
    /// DDMS itself calls for a bulk record.
    ///
    /// The DDMS has no operation on a record's versions (its only versions route is a GET listing), and versions
    /// belong to the storage service for every kind of record, so <see cref="RemovalScope.History"/> goes to
    /// storage's version purge and leaves the DDMS record itself untouched, which is exactly what that scope
    /// promises. Storage is a different service from a DDMS endpoint, though, and the DDMS paths carry no
    /// <c>/api/&lt;service&gt;/</c> prefix, so the storage default resolves only when the endpoint is the platform root.
    /// Otherwise the flow says where storage lives (<c>purgeVersionsPath</c>, <c>purgePath</c>), usually as an
    /// absolute URL, and without it the scope is refused, because guessing would send a delete somewhere nobody chose.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var paths = Held(targetId);
        if (scope == RemovalScope.History)
        {
            if (_routing.HistoryPath is not { } purgeVersions)
            {
                throw new RecordHeldException(
                    "a DDMS has no version purge, and this flow does not say where the storage service is. "
                    + "Declare target.protocolOptions.ddmsRoot (or a root for the DDMS under target.ddms) when the endpoint is the OSDU platform root, or "
                    + "purgeVersionsPath (an absolute URL such as https://<host>/api/storage/v2/records/{id}/versions) "
                    + "when it is the DDMS itself, before purging a record's history.");
            }

            return await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options) with { PurgeVersions = purgeVersions }, targetId, scope, ct).ConfigureAwait(false);
        }

        if (scope == RemovalScope.Everything && paths.Route is { Collection.Bulk: false } route)
        {
            if (_routing.StoragePurgePath is not { } purge)
            {
                throw new RecordHeldException(
                    $"{targetId} is in {route.Describe()}, whose DELETE is logical only, and this flow does not say where the storage service is to purge it. "
                    + "Declare target.protocolOptions.ddmsRoot (or a root for the DDMS under target.ddms) when the endpoint is the OSDU platform root, or "
                    + "purgePath (an absolute URL such as https://<host>/api/storage/v2/records/{id}) when it is the DDMS itself.");
            }

            return await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options) with { Purge = purge }, targetId, scope, ct).ConfigureAwait(false);
        }

        var url = _client.Url(paths.Delete, targetId);
        if (scope == RemovalScope.Everything)
        {
            url = OsduHttpClient.WithQuery(url, "purge", "true");
        }

        var result = await _client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return new DeleteOutcome(false, true, "record not found in OSDU");
        }

        return new DeleteOutcome(true, false, scope == RemovalScope.Everything
            ? "purged from OSDU (the record, every version and its bulk data)"
            : "removed from OSDU (reversible)");
    }

    /// <summary>
    /// Writes the record through its collection. A record of a bulk collection carries the bulk link the DDMS holds for
    /// it: the latest version is read first when the ledger knows the record was delivered, and when it does not, a
    /// refused write reads it once and is sent again if the DDMS holds a link after all (a delivery whose outcome was
    /// lost). The OSDU-owned data keys the flow preserves (<see cref="ProtocolOptions.PreserveDataKeys"/>) come from the
    /// same read.
    /// </summary>
    private async Task<(long? Version, int Status)> WriteRecordAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var document = (JsonObject)work.Document.DeepClone();
        var preserve = _options.PreserveDataKeys.Count > 0;
        var method = _options.RecordMethod ?? "POST";
        var read = work.ExistingVersion is not null && (preserve || paths.Bulk);
        JsonObject? stored = null;
        if (read)
        {
            stored = await RecordWriter.ReadAsync(_client, paths.Record, work.TargetId, ct).ConfigureAwait(false);
            if (stored is not null && preserve)
            {
                RecordWriter.Preserve(stored, document, _options.PreserveDataKeys);
            }
        }

        if (paths.Bulk)
        {
            Link(stored, document, work.TargetId);
        }

        try
        {
            return await RecordWriter.SendAsync(_client, _options, paths.Records, method, document, ct).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode == 400 && paths.Bulk && !read)
        {
            stored = await RecordWriter.ReadAsync(_client, paths.Record, work.TargetId, ct).ConfigureAwait(false);
            if (WellboreDdmsBulkLink.Of(stored) is null)
            {
                throw;
            }

            _logger.LogWarning(
                "The DDMS holds {TargetId} with a bulk link the ledger did not know of and refused the write without it; writing it again with the link: {Message}",
                work.TargetId, HeaderRedaction.RedactMessage(ex.Message));
            Link(stored, document, work.TargetId);
            return await RecordWriter.SendAsync(_client, _options, paths.Records, method, document, ct).ConfigureAwait(false);
        }
    }

    private void Link(JsonObject? stored, JsonObject document, string targetId)
    {
        if (!WellboreDdmsBulkLink.Carry(stored, document))
        {
            _logger.LogWarning(
                "The record {TargetId} is rendered with its own data.ExtensionProperties.wdms.bulkURI, which the DDMS manages; the link the DDMS holds is sent instead.",
                targetId);
        }
    }

    /// <summary>Why the record's bulk data has nowhere to go, or null when every bulk path is known.</summary>
    private static string? BulkProblem(string targetId, DdmsRecordPaths paths)
    {
        if (paths.Data is not null && paths.Sessions is not null && paths.SessionData is not null && paths.Session is not null)
        {
            return null;
        }

        return paths.Route is { Collection.Bulk: false } route
            ? $"{targetId} goes to {route.Describe()}, which holds records alone and takes no bulk data; deliver the record without a payload"
            : $"{targetId} has bulk data and the flow names no DDMS path for it (dataPath, sessionPath, sessionDataPath, sessionCommitPath)";
    }

    /// <summary>The calls for a record, a record the flow cannot route being held with the reason.</summary>
    private DdmsRecordPaths Held(string targetId)
    {
        try
        {
            return _routing.ForRecord(targetId);
        }
        catch (DeliveryException ex)
        {
            throw new RecordHeldException(ex.Message, ex);
        }
    }

    /// <summary>
    /// What a payload is checked for before its first request. Two ceilings bound a chunk. The estate's request body
    /// size is declared as <c>reliability.maxRequestBodyBytes</c> and can be raised where it is configured. The
    /// wellbore DDMS bulk shape (<see cref="WellboreDdmsBulkLimits"/>) cannot: it is the frame the service
    /// materialises, so a chunk can be small enough to send and still be too large to accept. The columns of all the
    /// chunks are checked against the record as the collection's DDMS checks them (<see cref="WellboreDdmsRules"/>). A
    /// session's chunks are also checked against each other, because the session aggregates them by row label and two
    /// chunks that give the same labels to different rows lose rows while the commit still succeeds
    /// (<see cref="WellboreDdmsSessionChunks"/>). Shapes are read from the parquet footers, never from the chunks'
    /// contents, when the payload is parquet and a ceiling or a column rule is in force or a session opens. They are
    /// returned so the committed bulk can be checked against them, and are null when they were not read.
    /// </summary>
    private async Task<IReadOnlyList<ParquetShape>?> PreflightAsync(JsonObject document, DdmsRecordPaths paths, IPayloadSource payload, IReadOnlyList<PayloadFile> chunks, bool session, CancellationToken ct)
    {
        var parquet = _options.PayloadContentType.Contains("parquet", StringComparison.OrdinalIgnoreCase);
        var readsShape = parquet && (session || _options.MaxChunkValues > 0 || _options.MaxChunkColumns > 0 || paths.Columns != DdmsBulkColumns.Unchecked);
        var measured = readsShape ? new List<WellboreDdmsSessionChunks.Chunk>(chunks.Count) : null;

        foreach (var chunk in chunks)
        {
            if (_requestBodyCeiling > 0 && chunk.Size > _requestBodyCeiling)
            {
                throw new RecordHeldException(
                    $"payload chunk {chunk.Index.ToString(CultureInfo.InvariantCulture)} is {chunk.Size.ToString(CultureInfo.InvariantCulture)} bytes, above the target's declared request body ceiling of {_requestBodyCeiling.ToString(CultureInfo.InvariantCulture)} bytes (reliability.maxRequestBodyBytes); re-chunk in prepare");
            }

            if (measured is null)
            {
                continue;
            }

            var shape = await ShapeAsync(payload, chunk, ct).ConfigureAwait(false);
            if (WellboreDdmsBulkLimits.Exceeded(chunk.Index, chunk.Path, shape.Rows, shape.Columns, _options.MaxChunkValues, _options.MaxChunkColumns) is { } held)
            {
                throw new RecordHeldException(held);
            }

            measured.Add(new WellboreDdmsSessionChunks.Chunk(chunk.Index, chunk.Path, shape));
        }

        if (measured is null)
        {
            return null;
        }

        var labels = measured.SelectMany(m => m.Shape.ColumnNames).Distinct(StringComparer.Ordinal).ToList();
        if (WellboreDdmsRules.ColumnsProblem(paths.Columns, document, labels) is { } columns)
        {
            throw new RecordHeldException(columns);
        }

        if (session && WellboreDdmsSessionChunks.Conflict(measured) is { } conflict)
        {
            throw new RecordHeldException(conflict);
        }

        return measured.Select(m => m.Shape).ToList();
    }

    /// <summary>
    /// Reads the bulk a session committed back (openapi wellbore_ddms, GET /ddms/v3/{collection}/{record_id}/data with
    /// <c>describe=true</c>, which answers the number of rows and the column names) and holds the record when the bulk
    /// lacks rows or columns its chunks carried. Returns the rows the bulk holds, or null when the target cannot describe
    /// it (a facade without the describe query, for instance): the delivery then stands unchecked, with a warning,
    /// rather than failing bulk data that did land.
    /// </summary>
    private async Task<long?> CommittedRowsAsync(string targetId, DdmsRecordPaths paths, string sessionId, IReadOnlyList<ParquetShape> shapes, CancellationToken ct)
    {
        var url = OsduHttpClient.WithQuery(_client.Url(paths.Data!, targetId), "describe", "true");
        long rows;
        List<string>? columns = null;
        try
        {
            var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 400, 404, 405, 422, 501 }, ct).ConfigureAwait(false);
            if ((int)result.Status is < 200 or >= 300 || result.Body.Length == 0)
            {
                LogUnchecked(targetId, sessionId, "HTTP " + ((int)result.Status).ToString(CultureInfo.InvariantCulture));
                return null;
            }

            var described = OsduHttpClient.ParseJson(result, url);
            if (described.ValueKind != System.Text.Json.JsonValueKind.Object
                || !described.TryGetProperty("numberOfRows", out var count)
                || !count.TryGetInt64(out rows))
            {
                LogUnchecked(targetId, sessionId, "the description carries no numberOfRows");
                return null;
            }

            if (described.TryGetProperty("columns", out var names) && names.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                columns = names.EnumerateArray()
                    .Where(name => name.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(name => name.GetString()!)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            LogUnchecked(targetId, sessionId, HeaderRedaction.RedactMessage(ex.Message));
            return null;
        }

        var shortfall = WellboreDdmsSessionChunks.Shortfall(
            targetId,
            sessionId,
            WellboreDdmsSessionChunks.ExpectedRows(shapes),
            columns is null ? [] : WellboreDdmsSessionChunks.ExpectedColumns(shapes),
            rows,
            (IReadOnlyCollection<string>?)columns ?? []);
        if (shortfall is not null)
        {
            throw new RecordHeldException(shortfall);
        }

        return rows;
    }

    private void LogUnchecked(string targetId, string sessionId, string reason)
        => _logger.LogWarning(
            "The bulk session {SessionId} committed for {TargetId} could not be described, so its rows were not checked against its chunks: {Reason}",
            sessionId, targetId, reason);

    /// <summary>Reads one chunk's shape from its footer. A chunk that does not parse holds the record: the service would refuse it too.</summary>
    private static async Task<ParquetShape> ShapeAsync(IPayloadSource payload, PayloadFile chunk, CancellationToken ct)
    {
        var opened = await payload.OpenAsync(chunk, ct).ConfigureAwait(false);
        Stream seekable;
        try
        {
            seekable = await ParquetFiles.EnsureSeekableAsync(opened, ct).ConfigureAwait(false);
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
                return await ParquetFiles.ReadShapeAsync(seekable, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RecordHeldException(
                    $"payload chunk {chunk.Index.ToString(CultureInfo.InvariantCulture)} ({Path.GetFileName(chunk.Path)}) is declared as parquet but its footer could not be read, so its shape cannot be checked against the target's bulk ceilings: {HeaderRedaction.RedactMessage(ex.Message)}",
                    ex);
            }
        }
    }

    private async Task<(int Sent, string SessionId)> SendSessionAsync(DeliveryWork work, DdmsRecordPaths paths, IPayloadSource payload, IReadOnlyList<PayloadFile> chunks, long? version, CancellationToken ct)
    {
        var createUrl = _client.Url(paths.Sessions!, work.TargetId);
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
                var url = _client.Url(paths.SessionData!, work.TargetId, sessionId);

                // A chunk sent twice into a session lands twice in the committed bulk, so a chunk whose outcome is
                // unclear fails the session (which is abandoned) rather than being resent.
                await _client.SendStreamAsync(HttpMethod.Post, url, () => OpenSync(payload, chunk), _options.PayloadContentType, chunk.Size > 0 ? chunk.Size : null, ct, idempotent: false).ConfigureAwait(false);
                sent++;
            }

            await CommitAsync(work.TargetId, paths, sessionId, ct).ConfigureAwait(false);
            return (sent, sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AbandonAsync(work.TargetId, paths, sessionId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Commits the session, and settles a commit the service answers 409 or 412 to by asking what state the session
    /// is actually in (openapi wellbore_ddms, GET /ddms/v3/{collection}/{record_id}/sessions/{session_id}).
    ///
    /// The commit is a PATCH and is never resent blind, so its outcome can be unclear: the connection went, a
    /// gateway answered 5xx after the service had acted, or an intermediary resent it and the second copy met a
    /// session that is no longer open (409 or 412). Treating any of those as a failure would fail a record whose
    /// bulk data did land, and send the whole payload again on the next try. The session's own state says which
    /// happened, so it is read rather than guessed: committed or committing is the commit that worked, anything
    /// else is a real failure.
    /// </summary>
    private async Task CommitAsync(string targetId, DdmsRecordPaths paths, string sessionId, CancellationToken ct)
    {
        var commitUrl = _client.Url(paths.Session!, targetId, sessionId);
        try
        {
            await _client.SendJsonAsync(HttpMethod.Patch, commitUrl, new JsonObject { ["state"] = "commit" }, null, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is OsduStatusException { StatusCode: 409 or 412 or >= 500 } || ex is DeliveryException { InnerException: HttpRequestException or IOException or TaskCanceledException })
        {
            var state = await SessionStateAsync(targetId, paths, sessionId, ct).ConfigureAwait(false);
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
    private async Task<string?> SessionStateAsync(string targetId, DdmsRecordPaths paths, string sessionId, CancellationToken ct)
    {
        try
        {
            var url = _client.Url(paths.Session!, targetId, sessionId);
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

    private async Task AbandonAsync(string targetId, DdmsRecordPaths paths, string sessionId)
    {
        try
        {
            var url = _client.Url(paths.Session!, targetId, sessionId);
            await _client.SendJsonAsync(HttpMethod.Patch, url, new JsonObject { ["state"] = "abandon" }, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            _logger.LogWarning("Could not abandon session {SessionId} for {TargetId}: {Message}", sessionId, targetId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>The request factory is synchronous; opening a blob stream is cheap and the copy is what streams.</summary>
    internal static Stream OpenSync(IPayloadSource payload, PayloadFile chunk)
        => payload.OpenAsync(chunk).GetAwaiter().GetResult();
}
