using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The ddms route (design.md sections 8.1 and 8.3, docs/interfaces-design.md sections 5.3 and 5.4): each record goes to
/// the collection of the DDMS serving its entity type (<see cref="DdmsRouting"/>), by the call pattern of that DDMS's
/// shape: the Wellbore DDMS v3 (<see cref="WellboreDdmsV3Shape"/>), the Well Delivery DDMS
/// (<see cref="WellDeliveryShape"/>), the Rock and Fluid Sample DDMS (<see cref="RafsShape"/>), the Production DDMS
/// historian (<see cref="ProductionTimeSeriesShape"/>), Seismic Store (<see cref="SeismicStoreShape"/>) or the Reservoir
/// Management DDMS (<see cref="ReservoirManagementShape"/>). A record's shape
/// checks it, and the data its DDMS keeps for it, before the first request, writes both, and says how the record is read
/// back, verified and removed. The protocol is named after the <c>osduWellLog</c> value a flow's <c>target.protocol</c>
/// gives it.
/// </summary>
public sealed class OsduDdmsProtocol : IDeliveryProtocol
{
    public const string MetadataStep = "metadata";
    public const string PayloadStep = "payload";

    private readonly OsduHttpClient _client;
    private readonly DdmsRouting _routing;
    private readonly TimeProvider _time;
    private readonly IDdmsShape _wellbore;
    private readonly IDdmsShape _wellDelivery;
    private readonly IDdmsShape _rafs;
    private readonly IDdmsShape _timeSeries;
    private readonly IDdmsShape _seismic;
    private readonly IDdmsShape _reservoirManagement;

    public OsduDdmsProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null, DdmsRouting? routing = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _routing = routing ?? DdmsRouting.Of(options);
        _time = time ?? TimeProvider.System;
        var context = new DdmsShapeContext(client, options, _routing, logger, requestBodyCeiling, _time);
        _wellbore = new WellboreDdmsV3Shape(context);
        _wellDelivery = new WellDeliveryShape(context);
        _rafs = new RafsShape(context);
        _timeSeries = new ProductionTimeSeriesShape(context);
        _seismic = new SeismicStoreShape(context);
        _reservoirManagement = new ReservoirManagementShape(context);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.Ddms;

    /// <summary>Where the protocol sends each record: the flow's DDMSs, every registration among them read.</summary>
    public DdmsRouting Routing => _routing;

    /// <summary>The attributes a batched storage read projects to see the links every shape keeps on a record.</summary>
    public static IReadOnlyList<string> LinkAttributes { get; } = ["data.ExtensionProperties", "data.DDMSDatasets"];

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var prepared = await PrepareAsync(work, ct).ConfigureAwait(false);
        return await SendAsync(work, prepared, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything the route checks before its first request, for a record and the data its DDMS keeps for it
    /// (<paramref name="work"/>'s <see cref="DeliveryWork.Payload"/>): where the record goes, and whatever its shape checks.
    /// A record that fails is held here, so a route that sends something else first (files, a manifest) checks it before
    /// that too.
    /// </summary>
    public async Task<PreparedDdmsWork> PrepareAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var paths = Held(work.TargetId);
        var state = await ShapeOf(paths).PrepareAsync(work, paths, ct).ConfigureAwait(false);
        return new PreparedDdmsWork(paths, state);
    }

    /// <summary>
    /// Writes what <paramref name="prepared"/> checked: the record through its collection when the work delivers it, then
    /// the data its DDMS keeps for it. <paramref name="work"/> may differ from the work that was prepared only in what a
    /// composed route learned since (its dataset list, the version its manifest wrote).
    /// </summary>
    public Task<DeliveryOutcome> SendAsync(DeliveryWork work, PreparedDdmsWork prepared, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(prepared);
        return ShapeOf(prepared.Paths).SendAsync(work, prepared.Paths, prepared.State, ct);
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
    {
        var paths = _routing.ForRecord(targetId);
        return ShapeOf(paths).VerifyAsync(paths, targetId, expectedVersion, ct);
    }

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
    {
        if (_routing.StorageReadPath(targetId) is { } storage)
        {
            return RecordWriter.ReadAsync(_client, storage, targetId, ct);
        }

        var paths = _routing.ForRecord(targetId);
        return ShapeOf(paths).ReadAsync(paths, targetId, ct);
    }

    /// <summary>The storage service's version list for a record no DDMS the flow reaches serves; null for a DDMS's record, whose DDMS keeps none.</summary>
    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => _routing.StorageReadPath(targetId) is { } storage
            ? RecordWriter.VersionsAsync(_client, storage, targetId, ct)
            : Task.FromResult<IReadOnlyList<long>?>(null);

    /// <summary>A record no DDMS the flow reaches serves, as the storage service held it at <paramref name="version"/>.</summary>
    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => _routing.StorageReadPath(targetId) is { } storage
            ? RecordWriter.ReadVersionAsync(_client, storage, targetId, version, ct)
            : throw new DeliveryException($"The target keeps no version history for {targetId}, so there is no version {version.ToString(CultureInfo.InvariantCulture)} to read.");

    /// <summary>
    /// Asks each DDMS the flow reaches for its service description, as its shape describes itself (<c>GET /about</c> of
    /// the Wellbore DDMS, <c>GET /info</c> of the others, RAFS's type catalogue, the historian's query service, Seismic
    /// Store's status and the flow's subproject, and the Reservoir Management DDMS's health check and a read behind its
    /// token), or the flow's own probe path. The target is reachable when every one of
    /// them answers.
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
            var path = probe;
            if (path.Contains(DdmsCatalog.PartitionToken, StringComparison.Ordinal))
            {
                // A Seismic Store tenant the flow names by its partition.
                if (_client.Header(Documents.FlowMapper.PartitionHeader) is not { Length: > 0 } partition)
                {
                    return new ProbeOutcome(false, 0, $"the flow names no Seismic Store tenant and sends no {Documents.FlowMapper.PartitionHeader} to take it from", probe);
                }

                path = path.Replace(DdmsCatalog.PartitionToken, UrlPath.EscapeSegment(partition), StringComparison.Ordinal);
            }

            var outcome = await RecordWriter.ProbeAsync(_client, path, ct).ConfigureAwait(false);
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
                Detail = _routing.Services.Count > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"all {_routing.Services.Count} DDMSs answered")
                    : string.Create(CultureInfo.InvariantCulture, $"the DDMS answered all {answered.Count} probes"),
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

    /// <summary>Removes the record as the shape of its DDMS removes one (see each shape for what each scope calls).</summary>
    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var paths = Held(targetId);
        return ShapeOf(paths).DeleteAsync(paths, targetId, scope, targetState, ct);
    }

    /// <summary>
    /// Carries the link the record <paramref name="targetId"/> keeps to the data its DDMS holds, from its stored version
    /// into <paramref name="document"/>, which rewrites it past the DDMS (a manifest). Returns false when the document
    /// rendered a link of its own that differs from the stored one, which the DDMS's is written over.
    /// </summary>
    public bool CarryLink(string targetId, JsonObject? stored, JsonObject document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(document);
        return ShapeOf(Held(targetId)).CarryLink(stored, document);
    }

    /// <summary>The call pattern of the DDMS a record goes to; paths the flow names are a Wellbore DDMS facade's.</summary>
    private IDdmsShape ShapeOf(DdmsRecordPaths paths) => paths.Shape switch
    {
        DdmsShape.WellboreDdmsV3 => _wellbore,
        DdmsShape.WellDeliveryV1 => _wellDelivery,
        DdmsShape.RafsV2 => _rafs,
        DdmsShape.ProductionTimeSeriesV1 => _timeSeries,
        DdmsShape.SeismicStoreV3 => _seismic,
        DdmsShape.ReservoirManagement => _reservoirManagement,
        _ => throw new InvalidOperationException($"The ddms route has no call pattern for the shape {paths.Shape}."),
    };

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

    /// <summary>The request factory is synchronous; opening a blob stream is cheap and the copy is what streams.</summary>
    internal static Stream OpenSync(IPayloadSource payload, PayloadFile chunk)
        => payload.OpenAsync(chunk).GetAwaiter().GetResult();
}

/// <summary>What the ddms route checked for one record before its first request, and what its send uses.</summary>
/// <param name="Paths">Where the record and its bulk data go.</param>
/// <param name="State">What the shape of the record's DDMS checked and its send reads.</param>
public sealed record PreparedDdmsWork(DdmsRecordPaths Paths, object? State);
