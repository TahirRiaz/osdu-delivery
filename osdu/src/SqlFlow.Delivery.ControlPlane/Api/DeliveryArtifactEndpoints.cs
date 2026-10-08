using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One artifact of a record (docs/atomic-delivery-plan.md, Artifacts): something a delivery created in OSDU, or set out to
/// create, with the unit of work that created it, what it is (<c>Role</c>, one of record, version, dataset, content, output,
/// dataspace, session, lock, rows, points, objects or run), the route's name for it within its unit (<c>Slot</c>), the OSDU id
/// or what finds it, the version the unit wrote and the one it replaced, and where it stands (<c>State</c>: intent, pending,
/// live, superseded, due, removed, restored, gone, kept or failed). <c>Open</c> says an undo may still take it;
/// <c>Exhausted</c> that its undo failed as often as the sweep tries, so only an operator's undo run takes it now.
/// <c>Note</c> is what the route or the undo had to say about it, redacted. <c>SettledUtc</c>, <c>SettledBy</c> and
/// <c>SettledRunId</c> say when an undo settled it, who ran that undo and in which run.
/// </summary>
public sealed record DeliveryArtifactDto(
    long ArtifactId, Guid UnitId, DateTime UnitStartedUtc, string Slot, string Role, string? TargetId, string? Locator,
    long? Version, long? PriorVersion, string State, bool Open, bool Exhausted, string? Note, int UndoAttempts, DateTime? NextUndoUtc,
    Guid? SubmissionId, Guid? CreatedRunId, DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? SettledUtc, Guid? SettledRunId, string? SettledBy);

/// <summary>
/// How many artifacts of a ledger, or of a source's ledgers added up, an undo may still take, by state: intents and pending ones
/// (deliveries under way, or abandoned ones the sweep has not reached), due (aborted, the undo not run yet), failed (the undo
/// refused or unreachable, tried again with backoff), and of those the exhausted ones, which only an operator's undo run with
/// force takes now. <c>ToUndo</c> is due and failed together.
/// </summary>
public sealed record DeliveryArtifactCountsDto(long Intent, long Pending, long Due, long Failed, long FailedExhausted, long ToUndo)
{
    public static DeliveryArtifactCountsDto Of(ArtifactCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return new(counts.Intent, counts.Pending, counts.Due, counts.Failed, counts.FailedExhausted, counts.ToUndo);
    }

    public DeliveryArtifactCountsDto Add(DeliveryArtifactCountsDto other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Intent + other.Intent, Pending + other.Pending, Due + other.Due, Failed + other.Failed, FailedExhausted + other.FailedExhausted, ToUndo + other.ToUndo);
    }
}

/// <summary>One interface of a source, its ledger identity, and its open artifacts counted by state.</summary>
public sealed record DeliveryInterfaceUndosDto(string? Interface, Guid FlowId, DeliveryArtifactCountsDto Counts);

/// <summary>
/// One record with open artifacts, as the flow's open undos list it: the record as the ledger holds it (null fields for a key
/// the ledger no longer holds a record of), its open artifacts counted by state, when the oldest was written, and when the
/// sweep next tries a failed one (null when none waits for a retry).
/// </summary>
public sealed record DeliveryOpenUndoRecordDto(
    Guid FlowId, Guid DeliveryKey, string? SourceKey, string? Label, string? TargetId, string? Status,
    long Intent, long Pending, long Due, long Failed, long FailedExhausted, DateTime OldestUtc, DateTime? NextUndoUtc);

/// <summary>
/// A flow's open undos in the partition the request names: its artifacts an undo may still take, counted by state for the
/// whole source (<c>Counts</c>) and for each interface (<c>Interfaces</c>), and a page of the records that have any, of the
/// interface <c>Interface</c> names (the one the request named, or the only one), the records with an exhausted undo first.
/// <c>Records</c> is null for a source of several interfaces when the request names none: records are listed one ledger at a time.
/// </summary>
public sealed record DeliveryOpenUndosDto(
    DeliveryArtifactCountsDto Counts, IReadOnlyList<DeliveryInterfaceUndosDto> Interfaces, string? Interface, Guid? FlowId,
    PagedResult<DeliveryOpenUndoRecordDto>? Records);

/// <summary>
/// What deliveries created in OSDU, as the ledger names it (docs/atomic-delivery-plan.md): a record's artifacts, newest first,
/// for its page, and a flow's open undos, the artifacts unfinished deliveries left that an undo may still take, with the records
/// that hold them. Every answer is read from the ledger's artifact table through its indexes; nothing here reaches OSDU. The undo
/// itself is the flow's <c>undo</c> operation, queued as any run of the flow is.
/// </summary>
public static class DeliveryArtifactEndpoints
{
    /// <summary>The artifacts of one record a read answers with unless asked for fewer.</summary>
    private const int DefaultArtifacts = 200;

    /// <summary>The most artifacts of one record a read answers with.</summary>
    private const int MaxArtifacts = 1000;

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/artifacts", ListRecordArtifactsAsync).WithName("ListDeliveryRecordArtifacts");
        delivery.MapGet("/flows/{pipelineId:guid}/undos", ListOpenUndosAsync).WithName("ListDeliveryOpenUndos");
    }

    /// <summary>
    /// The artifacts of one record, the newest first, at most <paramref name="max"/>. A record deleted from the ledger keeps its
    /// artifacts (the inventory names the ids it minted), so they are answered for it too; a key the ledger never held is 404.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryArtifactDto>>, ProblemHttpResult>> ListRecordArtifactsAsync(
        Guid flowId, Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var deliveryKey = new DeliveryKey(key);
        if (await ledger.GetRecordAsync(flowId, deliveryKey, ct).ConfigureAwait(false) is null
            && await ledger.FindPurgedAsync(flowId, deliveryKey, ct).ConfigureAwait(false) is null)
        {
            return TypedResults.Problem(detail: $"No record '{key}' in the ledger of flow '{flowId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var artifacts = await ledger.RecordArtifactsAsync(flowId, deliveryKey, Math.Clamp(max ?? DefaultArtifacts, 1, MaxArtifacts), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryArtifactDto>>(artifacts.Select(ToDto).ToList());
    }

    /// <summary>
    /// The open undos of a flow in the partition the request names: counted for the whole source and for each interface, with a
    /// page of the records of the interface the request names (or the only one) that hold artifacts an undo may still take.
    /// </summary>
    private static async Task<Results<Ok<DeliveryOpenUndosDto>, ProblemHttpResult>> ListOpenUndosAsync(
        Guid pipelineId, int? page, int? pageSize, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (unbound, problem) = await DeliveryEndpoints.ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (unbound is null)
        {
            return problem!;
        }

        var registry = await DeliveryEndpoints.RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false);
        var (bound, unpartitioned) = await DeliveryEndpoints.BindKeptAsync(ledger, unbound, partition, registry, ct).ConfigureAwait(false);
        if (unpartitioned is not null)
        {
            return unpartitioned;
        }

        var source = bound.Source;
        FlowDefinition? listed = null;
        if (!string.IsNullOrWhiteSpace(interfaceName) || source.Interfaces.Count == 1)
        {
            try
            {
                listed = source.Interface(string.IsNullOrWhiteSpace(interfaceName) ? null : interfaceName.Trim());
            }
            catch (DeliveryException ex)
            {
                return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound, title: "No such interface");
            }
        }

        var interfaces = new List<DeliveryInterfaceUndosDto>(source.Interfaces.Count);
        var total = DeliveryArtifactCountsDto.Of(ArtifactCounts.None);
        foreach (var flow in source.Interfaces)
        {
            var counts = DeliveryArtifactCountsDto.Of(await ledger.ArtifactCountsAsync(flow.Id, ct).ConfigureAwait(false));
            interfaces.Add(new DeliveryInterfaceUndosDto(flow.Interface, flow.Id, counts));
            total = total.Add(counts);
        }

        PagedResult<DeliveryOpenUndoRecordDto>? records = null;
        if (listed is not null)
        {
            var (p, size) = PageRequest.Normalize(page, pageSize);
            // A page past every record answers empty with the count, as a page of any listing does.
            var offset = (int)Math.Min((long)(p - 1) * size, int.MaxValue);
            var found = await ledger.OpenArtifactRecordsAsync(listed.Id, offset, size, ct).ConfigureAwait(false);
            IReadOnlyDictionary<DeliveryKey, RecordState> states = found.Records.Count == 0
                ? new Dictionary<DeliveryKey, RecordState>()
                : await ledger.GetRecordsAsync(listed.Id, found.Records.Select(r => r.Key).ToList(), ct).ConfigureAwait(false);
            records = new PagedResult<DeliveryOpenUndoRecordDto>(
                found.Records.Select(r => ToDto(listed.Id, r, states.GetValueOrDefault(r.Key))).ToList(), p, size, found.Total);
        }

        return TypedResults.Ok(new DeliveryOpenUndosDto(total, interfaces, listed?.Interface, listed?.Id, records));
    }

    internal static DeliveryArtifactDto ToDto(LedgerArtifact a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new DeliveryArtifactDto(
            a.ArtifactId, a.UnitId, a.UnitStartedUtc, a.Slot, a.Role, a.TargetId, a.Locator, a.Version, a.PriorVersion,
            ArtifactStatuses.Name(a.Status), ArtifactStatuses.IsOpen(a.Status),
            a.Status == ArtifactStatus.Failed && a.UndoAttempts >= ArtifactLimits.MaxUndoAttempts,
            a.Note is null ? null : HeaderRedaction.RedactMessage(a.Note), a.UndoAttempts, a.NextUndoUtc,
            a.SubmissionId, a.CreatedRunId, a.CreatedUtc, a.UpdatedUtc, a.SettledUtc, a.SettledRunId, a.SettledBy);
    }

    private static DeliveryOpenUndoRecordDto ToDto(Guid flowId, OpenArtifactRecord r, RecordState? record) => new(
        flowId, r.Key.Value, record?.SourceKey, record?.Label, record?.TargetId, record?.Status.ToString().ToLowerInvariant(),
        r.Intent, r.Pending, r.Due, r.Failed, r.FailedExhausted, r.OldestUtc, r.NextUndoUtc);
}
