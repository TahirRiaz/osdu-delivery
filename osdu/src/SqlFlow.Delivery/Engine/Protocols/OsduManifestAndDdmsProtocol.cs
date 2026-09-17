using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The composed route of a manifest and DDMS bulk data (docs/interfaces-design.md section 5.7): a batch's records go
/// through the ingestion workflow as the manifest route sends them (their files registered first), the run is settled
/// by reading each record back, and then each written record's bulk data goes through the collection of its DDMS, as
/// the ddms route sends it. Every record and its bulk data are checked against the DDMS's rules before the manifest is
/// sent. Ingestion writes a record through storage, past the DDMS, so a record that already holds bulk data carries the
/// link its DDMS keeps to it into the manifest (<see cref="OsduWellLogProtocol.CarryLink"/>: the Wellbore DDMS's bulk
/// link, RAFS's content datasets): without it the record would lose its link to the bulk data it holds.
/// </summary>
public sealed class OsduManifestAndDdmsProtocol : IDeliveryProtocol
{
    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly OsduManifestProtocol _manifest;
    private readonly OsduWellLogProtocol _ddms;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public OsduManifestAndDdmsProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null, DdmsRouting? routing = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _manifest = new OsduManifestProtocol(client, options.ForFiles(besideBulk: true), logger, requestBodyCeiling, _time);
        _ddms = new OsduWellLogProtocol(client, options, logger, requestBodyCeiling, _time, routing);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.OsduManifestAndDdms;

    public int MaxBatch => _manifest.MaxBatch;

    /// <summary>Where the protocol sends each record's bulk data: the flow's DDMSs, every registration among them read.</summary>
    public DdmsRouting Routing => _ddms.Routing;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await DeliverBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new DeliveryOutcome?[works.Count];
        var planned = new List<Planned>(works.Count);
        for (var i = 0; i < works.Count; i++)
        {
            var work = works[i];
            try
            {
                var files = work.DeliverPayload ? OsduFileAndDdmsProtocol.OptionalPart(work, PayloadParts.Files) : null;
                var bulk = work.DeliverPayload ? OsduFileAndDdmsProtocol.Part(work, PayloadParts.Bulk) : null;
                var sendsFiles = files is not null && work.Sends(files);
                var sendsBulk = bulk is not null && work.Sends(bulk);
                if (!work.DeliverMetadata && !sendsFiles && !sendsBulk)
                {
                    outcomes[i] = new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion, Detail = "every part is as OSDU holds it" };
                    continue;
                }

                // The bulk data and the record are checked against the DDMS before the manifest is sent.
                var bulkWork = work with
                {
                    DeliverMetadata = false,
                    DeliverPayload = sendsBulk,
                    Payload = bulk?.Source,
                    Parts = [],
                    ForcedParts = new HashSet<string>(StringComparer.Ordinal),
                };
                var prepared = sendsBulk ? await _ddms.PrepareAsync(bulkWork with { DeliverMetadata = work.DeliverMetadata }, ct).ConfigureAwait(false) : null;

                // A new record, or new files, go through the manifest: bulk data can only be written for a record storage holds.
                var writesRecord = work.DeliverMetadata || sendsFiles || (sendsBulk && work.ExistingVersion is null);
                var manifestWork = work with
                {
                    DeliverMetadata = writesRecord,
                    DeliverPayload = sendsFiles,
                    Payload = files?.Source,
                    Parts = [],
                    ForcedParts = new HashSet<string>(StringComparer.Ordinal),
                };
                planned.Add(new Planned(i, work, manifestWork, bulkWork, prepared, files, bulk, sendsFiles, sendsBulk));
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex);
            }
        }

        await CarryLinksAsync(planned, ct).ConfigureAwait(false);

        var sent = planned.Where(p => p.Failure is null && (p.ManifestWork.DeliverMetadata || p.ManifestWork.DeliverPayload)).ToList();
        var manifested = sent.Count == 0
            ? []
            : await _manifest.DeliverBatchAsync(sent.Select(p => p.ManifestWork).ToList(), ct).ConfigureAwait(false);
        for (var j = 0; j < sent.Count; j++)
        {
            sent[j].Manifested = manifested[j];
        }

        foreach (var item in planned)
        {
            outcomes[item.Index] = await FinishAsync(item, ct).ConfigureAwait(false);
        }

        return outcomes.Select(o => o!).ToList();
    }

    /// <summary>
    /// The link each record's DDMS keeps to its bulk data, carried from the stored records into the manifest of every
    /// record that already holds bulk data (<see cref="OsduWellLogProtocol.CarryLink"/>). The records are read in one
    /// batched read.
    /// </summary>
    private async Task CarryLinksAsync(List<Planned> planned, CancellationToken ct)
    {
        var updates = planned.Where(p => p.Failure is null && p.ManifestWork.DeliverMetadata && p.Work.ExistingVersion is not null).ToList();
        if (updates.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, JsonObject> stored;
        try
        {
            stored = await RecordWriter.ReadManyAsync(
                _client, _options.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath, updates.Select(p => p.Work.TargetId).ToList(), OsduWellLogProtocol.LinkAttributes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            // Without the stored link the manifest could drop it, so these records wait for the next try.
            foreach (var item in updates)
            {
                item.Failure = ex;
            }

            return;
        }

        foreach (var item in updates)
        {
            if (!stored.TryGetValue(item.Work.TargetId, out var record))
            {
                continue;
            }

            var document = (JsonObject)item.ManifestWork.Document.DeepClone();
            bool agreed;
            try
            {
                agreed = _ddms.CarryLink(item.Work.TargetId, record, document);
            }
            catch (RecordHeldException ex)
            {
                item.Failure = ex;
                continue;
            }

            if (!agreed)
            {
                _logger.LogWarning(
                    "The record {TargetId} is rendered with its own link to the bulk data its DDMS keeps, which the DDMS manages; the link the DDMS holds goes into the manifest instead.",
                    item.Work.TargetId);
            }

            item.ManifestWork = item.ManifestWork with { Document = document };
            item.BulkWork = item.BulkWork with { Document = document };
        }
    }

    /// <summary>One record's outcome: the manifest's, then, when the record is in storage, its bulk data's.</summary>
    private async Task<DeliveryOutcome> FinishAsync(Planned item, CancellationToken ct)
    {
        if (item.Failure is { } failed)
        {
            return DeliveryOutcome.Failed(failed);
        }

        var manifested = item.Manifested;
        if (manifested is { Failure: { } failure })
        {
            return manifested;
        }

        var version = manifested?.TargetVersion ?? item.Work.ExistingVersion;
        var returned = new Dictionary<string, string>(manifested?.Returned ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var steps = new List<DeliveryStep>(manifested?.Steps ?? []);
        var detail = new List<string>();
        if (manifested?.Detail is { } manifestDetail)
        {
            detail.Add(manifestDetail);
        }

        if (item.SendsFiles)
        {
            returned[PayloadParts.StateKey(item.Files!.Payload)] = item.Files.Hash;
        }

        DeliveryOutcome? bulk = null;
        if (item.SendsBulk)
        {
            try
            {
                bulk = await _ddms.SendAsync(item.BulkWork with { ExistingVersion = version }, item.Prepared!, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                // The record stands as the manifest wrote it; the bulk data is sent again on the next try, whose manifest
                // step resumes the finished run rather than ingesting the record again.
                return DeliveryOutcome.Failed(ex, steps);
            }

            foreach (var (name, value) in bulk.Returned)
            {
                returned[name] = value;
            }

            returned[PayloadParts.StateKey(item.Bulk!.Payload)] = item.Bulk.Hash;
            steps.AddRange(bulk.Steps);
            version = bulk.TargetVersion ?? version;
            if (bulk.Detail is { } bulkDetail)
            {
                detail.Add(bulkDetail);
            }
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = manifested?.MetadataDelivered ?? false,
            PayloadDelivered = item.SendsFiles || (bulk?.PayloadDelivered ?? false),
            TargetVersion = version,
            ChunksSent = (manifested?.ChunksSent ?? 0) + (bulk?.ChunksSent ?? 0),
            Detail = detail.Count == 0 ? null : string.Join("; ", detail),
            Returned = returned,
            Steps = steps,
        };
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => _manifest.VerifyAsync(targetId, expectedVersion, ct);

    int IDeliveryProtocol.MaxVerifyBatch => OsduRecordProtocol.MaxVerifyBatch;

    /// <summary>The records are in storage, as every record a DDMS keeps is, so they verify in batched storage reads.</summary>
    public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
        => _manifest.VerifyBatchAsync(requests, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => _manifest.ReadAsync(targetId, ct);

    /// <summary>The Workflow service and every DDMS the flow reaches; the target is reachable when all of them answer.</summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        var workflow = await _manifest.ProbeAsync(ct).ConfigureAwait(false);
        if (!workflow.Reachable)
        {
            return workflow;
        }

        var ddms = await _ddms.ProbeAsync(ct).ConfigureAwait(false);
        return ddms.Reachable ? ddms with { Path = workflow.Path + ", " + ddms.Path } : ddms;
    }

    public Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
        => _manifest.InvalidLegalTagsAsync(tags, ct);

    /// <summary>
    /// The DDMS removes a record it keeps bulk data for, and with everything its bulk data (<c>?purge=true</c>); the
    /// datasets the manifest's files were registered as go too when everything goes.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        var outcome = await _ddms.DeleteAsync(targetId, scope, targetState, ct).ConfigureAwait(false);
        return scope == RemovalScope.Everything
            ? await FileUploads.WithDatasetsDeletedAsync(_client, _options, outcome, targetState, ct).ConfigureAwait(false)
            : outcome;
    }

    private sealed class Planned(
        int index, DeliveryWork work, DeliveryWork manifestWork, DeliveryWork bulkWork, PreparedDdmsWork? prepared, WorkPayloadPart? files, WorkPayloadPart? bulk, bool sendsFiles, bool sendsBulk)
    {
        public int Index { get; } = index;

        public DeliveryWork Work { get; } = work;

        public DeliveryWork ManifestWork { get; set; } = manifestWork;

        public DeliveryWork BulkWork { get; set; } = bulkWork;

        public PreparedDdmsWork? Prepared { get; } = prepared;

        public WorkPayloadPart? Files { get; } = files;

        public WorkPayloadPart? Bulk { get; } = bulk;

        public bool SendsFiles { get; } = sendsFiles;

        public bool SendsBulk { get; } = sendsBulk;

        public DeliveryOutcome? Manifested { get; set; }

        public Exception? Failure { get; set; }
    }
}
