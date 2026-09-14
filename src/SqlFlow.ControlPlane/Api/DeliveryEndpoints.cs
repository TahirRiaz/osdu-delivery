using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Drops;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A delivery flow's record counts by state, drift, throughput, and its last submission: the flow's dashboard card.</summary>
public sealed record DeliveryFlowStatsDto(
    Guid PipelineId, string FlowName, Guid FlowId, long Total, long Pending, long Delivering, long Delivered, long Held, long Failed,
    long Deleted, long Drifted, long DeliveredLast24h, DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, long Submissions,
    DeliverySubmissionDto? LastSubmission);

/// <summary>One drop as the ledger received it and what became of it.</summary>
public sealed record DeliverySubmissionDto(
    Guid SubmissionId, Guid FlowId, string FlowName, string MappingReference, string RenderContext, string DropLocation,
    string ParametersJson, long RecordCount, string Status, DateTime ReceivedUtc, DateTime? StartedUtc, DateTime? CompletedUtc,
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held, long Failed, string? Error,
    string? WorkLocation, int BatchCount, int Partitions, string? Reference = null);

/// <summary>One retrieval run of a retrieval flow: the window it covered, where its files went, and its outcome.</summary>
public sealed record DeliveryRetrievalDto(
    long RetrievalId, Guid FlowId, string FlowName, Guid? RunId, string Actor, string Kinds, string? Query, string? WindowField,
    DateTime? WindowFrom, DateTime? WindowTo, string Location, string? ManifestLocation, string Status, long Records, int Files, long Bytes,
    DateTime StartedUtc, DateTime? CompletedUtc, string? Error);

/// <summary>One work batch of a submission: a file of rendered documents and how far its drain got.</summary>
public sealed record DeliveryWorkBatchDto(
    Guid SubmissionId, int Index, string Location, int RecordCount, string Status, string? LeaseOwner, DateTime? LeaseExpiresUtc, Guid? RunId,
    DateTime CreatedUtc, DateTime? StartedUtc, DateTime? CompletedUtc, long Delivered, long Held, long Failed, long Retrying, string? Error);

/// <summary>The current state of one deliverable: what OSDU holds for it, what is pending, and why it is where it is.</summary>
public sealed record DeliveryRecordDto(
    Guid DeliveryKey, Guid FlowId, string SourceKey, string? Label, string MappingName, string? RenderContext,
    string? SourceFingerprint, DateTime? SourceModifiedUtc, string? MetadataHash, string? PayloadHash, DateTime? PayloadModifiedUtc, string? TargetId, long? TargetVersion, string Status,
    DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, string? LastVerifyOutcome, string? LeaseOwner, DateTime? LeaseExpiresUtc,
    Guid? LastSubmissionId, int AttemptCount, DateTime? NextAttemptUtc, string? LastError, bool HasPendingDocument,
    bool PendingMetadata, bool PendingPayload, string? PendingPayloadLocation, bool Blocked, DateTime CreatedUtc, DateTime UpdatedUtc,
    string? PendingDocumentRef, int? WorkBatch, JsonElement? TargetState, JsonElement? PendingSteps);

/// <summary>A record with the pipeline it belongs to. The pending document itself lives in the submission's work batches on
/// storage, which the nodes read; its reference and batch are on the record.</summary>
public sealed record DeliveryRecordDetailDto(
    DeliveryRecordDto Record, Guid? PipelineId, Guid? RepoId, string? FlowName);

/// <summary>One delivery try, as the append-only history holds it: its outcome, and every step with what the target returned.</summary>
public sealed record DeliveryAttemptDto(
    long AttemptId, Guid DeliveryKey, Guid? SubmissionId, Guid? RunId, string Worker, DateTime StartedUtc, DateTime CompletedUtc,
    string Outcome, string Phase, string? MetadataHash, string? PayloadHash, long? TargetVersion, string? Error, JsonElement? Result, int? WorkBatch);

/// <summary>One entry of the audit trail: who did what, when, with which inputs, and how it ended.</summary>
public sealed record DeliveryActivityDto(
    long ActivityId, Guid FlowId, string FlowName, string Kind, string Actor, DateTime StartedUtc, DateTime? CompletedUtc,
    string Outcome, string? ParametersJson, Guid? SubmissionId, Guid? DeliveryKey, Guid? RunId, string? Summary, string? Log);

/// <summary>A submission with the attempts it produced and the runs that carried it.</summary>
public sealed record DeliverySubmissionDetailDto(
    DeliverySubmissionDto Submission, Guid? PipelineId, IReadOnlyList<Guid> RunIds);

/// <summary>A mapping document as the sync found it in a repository.</summary>
public sealed record DeliveryMappingDto(
    Guid Id, Guid RepoId, string Reference, string Name, string Version, string Kind, string RelativePath, string ContentHash,
    string Status, string? Message, JsonElement Summary, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>A mapping document with its text.</summary>
public sealed record DeliveryMappingDetailDto(DeliveryMappingDto Mapping, string Yaml);

/// <summary>A type a cache flow declares: what is cached, which paths are kept, what a change does, and how many records the current version holds.</summary>
public sealed record DeliveryCacheTypeDto(string Name, string EntityType, string Kind, string? Query, JsonElement Fields, string OnChange, long Items);

/// <summary>A schedule that refreshes a cache: its cadence, or that it fires behind other schedules.</summary>
public sealed record DeliveryCacheScheduleDto(Guid Id, string Name, string? Cron, int? IntervalSeconds, bool Chained);

/// <summary>
/// A cache as its cache flow declares it and its runs keep it: the repository and file that define it (the file is where
/// what is cached is changed), the flow's pipeline, the OSDU endpoint reference it is captured from, the schedules that
/// refresh it, its types, its current version with the run that captured it, and how many versions it has.
/// </summary>
public sealed record DeliveryCacheDto(
    string Name, Guid RepoId, string RepoName, string RelativePath, Guid? PipelineId, string? Endpoint, bool MakeCurrent,
    IReadOnlyList<DeliveryCacheTypeDto> Types, IReadOnlyList<DeliveryCacheScheduleDto> Schedules, DeliveryCacheVersionDto? Current, int Versions);

/// <summary>
/// One cache change and what happens about it: the cached record and path that moved, the value before and after,
/// how many delivered manifest rows it reaches, and how far the rollout has carried it.
/// </summary>
public sealed record DeliveryUpdateTagDto(
    long TagId, string Kind, string Cache, string TypeName, string ItemId, string Path, string Change, string? OldValue, string? NewValue,
    string? FromVersion, string ToVersion, string Mode, string Status, string Summary, long AffectedRecords, long Processed,
    long Remaining, DateTime DetectedUtc, DateTime? DecidedUtc, string? DecidedBy, DateTime? StartedUtc, DateTime? CompletedUtc);

/// <summary>A decision on a set of tags: approve lets the next run carry the update, reject leaves OSDU as it is.</summary>
public sealed record DeliveryTagDecisionRequest(IReadOnlyList<long> TagIds, bool Approve);

public sealed record DeliveryTagDecisionResult(int Decided, bool Approved);

/// <summary>One cached value a record was built from, for its history page: the cache, the cached record and path, and what it held.</summary>
public sealed record DeliveryCacheUseDto(string Cache, string TypeName, string ItemId, string Path, string Kind, string Value);

/// <summary>One cached record: its OSDU id and the values captured at the declared paths, as one version of its cache holds it.</summary>
public sealed record DeliveryCachedItemDto(
    long ItemId, string Cache, string Version, string TypeName, string EntityType, string RecordId, JsonElement Fields);

/// <summary>One type a cache version holds, and how many records of it.</summary>
public sealed record DeliveryCacheVersionTypeDto(string Name, string EntityType, long Items);

/// <summary>
/// One version of a cache: when it was captured, whether it is the version deliveries render against, the version that was
/// current when it was captured, the run that captured it and who asked (null run for an import from files), where the
/// content came from, and what it holds.
/// </summary>
public sealed record DeliveryCacheVersionDto(
    string Cache, string Version, int Sequence, DateTime CapturedUtc, bool Current, string? PreviousVersion, Guid? RunId, string CapturedBy,
    string Origin, long Items, IReadOnlyList<DeliveryCacheVersionTypeDto> Types);

/// <summary>
/// What changed in a cache between two versions: counts per type and a page of the records that differ. The counts follow
/// the type and search filters but not the change filter.
/// </summary>
public sealed record DeliveryCacheDiffDto(
    string Cache, string FromVersion, string ToVersion, long Changed, long Added, long Removed, IReadOnlyList<DeliveryCacheDiffTypeDto> Types,
    PagedResult<DeliveryCacheDiffItemDto> Items);

/// <summary>How many records of one cached type changed, arrived and left between the two versions.</summary>
public sealed record DeliveryCacheDiffTypeDto(string TypeName, long Changed, long Added, long Removed);

/// <summary>
/// One cached record that differs between the two versions: changed, added or removed, the captured values on each side
/// (null on the side that does not hold it), and the captured names whose value moved.
/// </summary>
public sealed record DeliveryCacheDiffItemDto(
    string TypeName, string EntityType, string RecordId, string Change, JsonElement? Before, JsonElement? After, IReadOnlyList<string> ChangedFields);

/// <summary>One version in a cache's history: the version captured before it, and how many records it changed, added and removed against that one.</summary>
public sealed record DeliveryCacheHistoryEntryDto(DeliveryCacheVersionDto Version, string? Before, long Changed, long Added, long Removed);

/// <summary>
/// A submission. Either the manifest notification (<c>drop</c>: the preparing side has finished a drop and asks for it to
/// be delivered) or inline records (<c>records</c>: a source sends the records themselves, each in the shape of a mapping
/// fixture, design.md section 3.4). The flow is named by pipeline id, or by repository and flow name, or by flow name
/// alone when it is unique. <c>operation</c> is deliver (the default) or plan. <c>submissionId</c> is the idempotency
/// key of inline records; a drop's is the one its manifest carries.
/// </summary>
/// <remarks>
/// <c>reference</c> is the caller's own name for this submission (a filename, a ticket, a job id): stored, searchable,
/// never interpreted, and part of the request the <c>submissionId</c> names, so a repeat carrying a different one is a
/// conflict. A drop's reference is the one its manifest carries, as its submission id is.
/// </remarks>
public sealed record DeliverySubmissionRequest(
    Guid? PipelineId, Guid? RepoId, string? Flow, string? Drop, IReadOnlyDictionary<string, string>? Parameters, bool Force = false, string? Pool = null,
    JsonElement? Records = null, Guid? SubmissionId = null, string? Operation = null, string? Reference = null);

/// <summary>
/// A submission was accepted: the run that takes it, the submission id when the records came inline, and whether this
/// answers a repeat of a request already accepted (the run is then the one that request started).
/// </summary>
public sealed record DeliverySubmissionAccepted(Guid RunId, Guid PipelineId, string FlowName, string Status, Guid? SubmissionId = null, bool Replayed = false);

/// <summary>An inline submission's records as the ledger holds them, with who sent them, where a run wrote them, and the runs that took them.</summary>
public sealed record DeliveryInlineSubmissionDto(
    Guid SubmissionId, Guid FlowId, string FlowName, Guid? PipelineId, string MappingReference, string Operation, bool Force, string ParametersJson,
    int RecordCount, long ChildRowCount, int ContentBytes, string ContentHash, DateTime ReceivedUtc, string ReceivedBy, string? DropLocation,
    DateTime? WrittenUtc, IReadOnlyList<Guid> RunIds, JsonElement Records, string? Reference = null);

/// <summary>One parameter a flow declares, for a caller filling in a submission.</summary>
public sealed record DeliveryFlowParameterDto(string Name, bool Required, string? Default, string? Description);

/// <summary>
/// One delivery flow as the manual submission page lists it: whether its document offers manual submission
/// (<c>source.manualSubmission</c>), what it renders with and the template version that mapping fills (null while the
/// mapping is not synced or is invalid), the parameter values a submission has to carry, and the payload its records point
/// at when it streams one. A flow that does not offer it is listed only when the caller asks for all of them, and says why.
/// </summary>
public sealed record DeliveryManualFlowDto(
    Guid PipelineId, Guid RepoId, string FlowName, string? Batch, string MappingReference, string Protocol,
    bool AcceptsRecords, string? RecordsRefusal, IReadOnlyList<DeliveryFlowParameterDto> Parameters, string? PayloadName,
    string? TemplateKind, string? TemplateVersion);

/// <summary>The template version a flow's mapping fills, and whether the catalog holds it: a run renders only with a saved version.</summary>
public sealed record DeliverySourceTemplateDto(string Kind, string Version, bool Saved);

/// <summary>
/// One mapping entry reading a column. <c>Target</c> is the template variable the entry fills. <c>Role</c> says how it reads
/// the column: <c>value</c> (the column's value, after the modifiers, is what the entry writes), <c>findBy</c> (the column's
/// value, after the modifiers, finds the cached record the entry writes from, on the <c>FindBy</c> line) or
/// <c>appliesWhen</c> (the column decides whether the entry applies). <c>Source</c> is the entry's source as the mapping
/// writes it, null for a static entry; <c>Required</c> says whether an empty value or a cache miss holds the record.
/// </summary>
public sealed record DeliverySourceColumnUseDto(
    string Target, string Role, string? Source, bool Required, IReadOnlyList<string> Modifiers, string? FindBy, string? AppliesWhen);

/// <summary>A column a record carries: whether the dataset key or the label reads it, and every entry that does.</summary>
public sealed record DeliverySourceColumnDto(string Name, bool Key, bool Label, IReadOnlyList<DeliverySourceColumnUseDto> Uses);

/// <summary>An array a child dataset's rows fill, one item per row, and whether a record with no rows is held.</summary>
public sealed record DeliverySourceRepeaterDto(string Target, bool Required);

/// <summary>A child dataset a record carries rows of under <c>datasets</c>: the arrays its rows fill, and the columns read from each row.</summary>
public sealed record DeliverySourceDatasetDto(string Name, IReadOnlyList<DeliverySourceRepeaterDto> Fills, IReadOnlyList<DeliverySourceColumnDto> Columns);

/// <summary>
/// What a source sends a flow (design.md section 3.4, docs/delivery/mapping-templates.md): whether the flow takes records
/// inline and why not, the parameters it declares, the template version its pinned mapping fills, the dataset's system, key
/// and label, every column the mapping reads from the dataset row and from each child dataset with the template variables
/// each fills, the column the flow versions rows by, and the ceilings of one inline submission. <c>MappingProblem</c> says
/// why records would not render: the catalog cannot read the pinned mapping (the columns are then unknown), or the template
/// version it pins is not saved. <c>PayloadName</c> is the payload the flow streams, which every record then points at
/// under <c>files</c>, with <c>PayloadHashRequired</c> saying whether each has to carry a content hash and
/// <c>PayloadRoots</c> where the files may sit.
/// </summary>
public sealed record DeliverySourceContractDto(
    Guid PipelineId, string FlowName, string MappingReference, string Protocol, bool AcceptsRecords, string? RecordsRefusal,
    IReadOnlyList<DeliveryFlowParameterDto> Parameters,
    DeliverySourceTemplateDto? Template, string? System, IReadOnlyList<string> Key, string? Label,
    IReadOnlyList<DeliverySourceColumnDto> Columns, IReadOnlyList<DeliverySourceDatasetDto> Datasets,
    string? LastModifiedColumn, string? FingerprintColumn, string? MappingProblem,
    int MaxRecords, int MaxChildRows, int MaxContentBytes,
    string? PayloadName, bool PayloadHashRequired, IReadOnlyList<string> PayloadRoots);

/// <summary>A run was queued for a record-scoped operation (redeliver, verify).</summary>
public sealed record DeliveryRunAccepted(Guid RunId, string Status);

/// <summary>A compute task was queued for a target-side operation (probe, read-back, delete).</summary>
public sealed record ComputeTaskAccepted(Guid TaskId, string Status);

public sealed record DeliveryReleaseRequest(IReadOnlyList<Guid>? Keys);

public sealed record DeliveryReleaseResult(int Released);

/// <summary>Redeliver: <c>scope</c> is all, metadata or payload; <c>run</c> queues the deliver run that sends it.</summary>
public sealed record DeliveryRedeliverRequest(string? Scope = null, bool Run = true, string? Pool = null);

public sealed record DeliveryRedeliverResult(int Marked, Guid? RunId);

/// <summary>
/// Where a flow's records actually live: the endpoint and data partition every removal in the GUI names before it
/// runs, with the exact call each scope makes. The endpoint is reported as the flow declares it, secret references
/// and all, because that reference is what identifies the environment; no credential or header value is exposed.
/// </summary>
public sealed record DeliveryTargetDto(
    Guid PipelineId, string FlowName, string Endpoint, string? DataPartition, string Protocol, string AuthType,
    string RecordPath, string HistoryPath, string EverythingPath);

/// <summary>The listing a removal is aimed at, the same filter the records list is built from.</summary>
public sealed record DeliveryRecordFilterDto(
    string? Status, string? Search, string? Mode, Guid? SubmissionId, Guid? RunId, bool Drifted = false);

/// <summary>
/// A removal of one or many records. <c>scope</c> is record, history or everything. The records are named either
/// by <c>keys</c> or by <c>filter</c> (every record the listing matches), never both. <c>expected</c> is the count
/// the operator was shown: when it no longer matches what the filter resolves to, the removal is refused rather
/// than run against a set that changed underneath them.
/// </summary>
public sealed record DeliveryRemovalRequest(
    string? Scope, IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected);

/// <summary>A removal was queued on a node: the task to watch, and how many records it will act on.</summary>
public sealed record DeliveryRemovalAccepted(Guid TaskId, string Status, string Scope, int Records);

/// <summary>What a removal would act on, for the confirmation the operator sees before asking for it.</summary>
public sealed record DeliveryRemovalPreview(
    string Scope, int Records, int InOsdu, int NeverDelivered, bool Capped, DeliveryTargetDto Target);

public sealed record DeliveryPruneRequest(int OlderThanDays);

public sealed record DeliveryPruneResult(int AttemptsPruned);

/// <summary>
/// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail
/// of runs and interventions, the mappings and snapshots the repositories hold, and the interventions themselves
/// (release, redeliver, verify, read back, delete). Reads are answered from the catalog's indexed ledger tables;
/// interventions either act on the ledger directly under the caller's name, or queue a run or a compute task for a
/// node, so nothing here ever talks to OSDU itself.
/// </summary>
public static class DeliveryEndpoints
{
    private const int MaxAttempts = 500;

    /// <summary>How often an inline submission's insert is tried when it loses to a concurrent request for the same id.</summary>
    private const int MaxAcceptAttempts = 3;

    /// <summary>Delivery flows the manual submission listing parses at once.</summary>
    private const int MaxManualSubmissionFlows = 500;

    /// <summary>Cached type declarations the cache listing reads at once, across every cache.</summary>
    private const int MaxCacheDefinitions = 5000;

    public static RouteGroupBuilder MapDeliveryReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        delivery.MapGet("/flows/{pipelineId:guid}/stats", GetStatsAsync).WithName("GetDeliveryFlowStats");
        delivery.MapGet("/flows/{pipelineId:guid}/records", ListRecordsAsync).WithName("ListDeliveryRecords");
        delivery.MapGet("/flows/{pipelineId:guid}/target", GetTargetAsync).WithName("GetDeliveryTarget");
        delivery.MapGet("/flows/{pipelineId:guid}/submissions", ListSubmissionsAsync).WithName("ListDeliverySubmissions");
        delivery.MapGet("/flows/{pipelineId:guid}/retrievals", ListRetrievalsAsync).WithName("ListDeliveryRetrievals");
        delivery.MapGet("/flows/{pipelineId:guid}/source-contract", GetSourceContractAsync).WithName("GetDeliverySourceContract");
        delivery.MapGet("/manual-submission/flows", ListManualSubmissionFlowsAsync).WithName("ListDeliveryManualSubmissionFlows");
        delivery.MapGet("/submissions/{submissionId:guid}/content", GetSubmissionContentAsync).WithName("GetDeliverySubmissionContent");
        delivery.MapGet("/records/{key:guid}", GetRecordAsync).WithName("GetDeliveryRecord");
        delivery.MapGet("/records/{key:guid}/attempts", ListRecordAttemptsAsync).WithName("ListDeliveryRecordAttempts");
        delivery.MapGet("/records/{key:guid}/activities", ListRecordActivitiesAsync).WithName("ListDeliveryRecordActivities");
        delivery.MapGet("/submissions/{submissionId:guid}", GetSubmissionAsync).WithName("GetDeliverySubmission");
        delivery.MapGet("/submissions/{submissionId:guid}/attempts", ListSubmissionAttemptsAsync).WithName("ListDeliverySubmissionAttempts");
        delivery.MapGet("/submissions/{submissionId:guid}/batches", ListSubmissionBatchesAsync).WithName("ListDeliverySubmissionBatches");
        delivery.MapGet("/activities", ListActivitiesAsync).WithName("ListDeliveryActivities");
        delivery.MapGet("/activities/{activityId:long}", GetActivityAsync).WithName("GetDeliveryActivity");
        delivery.MapGet("/mappings", ListMappingsAsync).WithName("ListDeliveryMappings");
        delivery.MapGet("/mappings/{mappingId:guid}", GetMappingAsync).WithName("GetDeliveryMapping");
        delivery.MapGet("/caches", ListCachesAsync).WithName("ListDeliveryCaches");
        delivery.MapGet("/cache/items", ListCachedItemsAsync).WithName("ListDeliveryCachedItems");
        delivery.MapGet("/cache/versions", ListCacheVersionsAsync).WithName("ListDeliveryCacheVersions");
        delivery.MapGet("/cache/diff", CompareCacheVersionsAsync).WithName("CompareDeliveryCacheVersions");
        delivery.MapGet("/cache/history", ListCacheHistoryAsync).WithName("ListDeliveryCacheHistory");
        delivery.MapGet("/cache/tags", ListUpdateTagsAsync).WithName("ListDeliveryUpdateTags");
        delivery.MapGet("/records/{key:guid}/cache", ListRecordCacheUsesAsync).WithName("ListDeliveryRecordCacheUses");
        return group;
    }

    public static RouteGroupBuilder MapDeliveryWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        // Inline records ride in the body, so the route reads a larger body than the default and no larger than that.
        delivery.MapPost("/submissions", SubmitAsync).WithName("SubmitDeliveryDrop")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(InlineRecords.MaxRequestBytes));
        delivery.MapPost("/flows/{pipelineId:guid}/release", ReleaseFlowAsync).WithName("ReleaseDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/probe", ProbeAsync).WithName("ProbeDeliveryTarget");
        delivery.MapPost("/cache/tags/decide", DecideUpdateTagsAsync).WithName("DecideDeliveryUpdateTags");
        delivery.MapPost("/records/{key:guid}/release", ReleaseRecordAsync).WithName("ReleaseDeliveryRecord");
        delivery.MapPost("/records/{key:guid}/redeliver", RedeliverAsync).WithName("RedeliverDeliveryRecord");
        delivery.MapPost("/records/{key:guid}/verify", VerifyRecordAsync).WithName("VerifyDeliveryRecord");
        delivery.MapPost("/records/{key:guid}/read", ReadRecordAsync).WithName("ReadDeliveryRecordBack");
        delivery.MapPost("/records/{key:guid}/delete", DeleteRecordAsync).WithName("DeleteDeliveryRecord");
        delivery.MapPost("/flows/{pipelineId:guid}/records/remove", RemoveRecordsAsync).WithName("RemoveDeliveryRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/records/remove/preview", PreviewRemovalAsync).WithName("PreviewDeliveryRemoval");
        delivery.MapPost("/ledger/prune", PruneAsync).WithName("PruneDeliveryLedger").RequireAuthorization("admin");
        return group;
    }

    // ---- Reads ---------------------------------------------------------------------------------------------------

    private static async Task<Results<Ok<DeliveryFlowStatsDto>, ProblemHttpResult>> GetStatsAsync(
        Guid pipelineId, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var stats = await ledger.StatsAsync(flow.FlowId, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryFlowStatsDto(
            flow.Pipeline.Id, flow.Pipeline.Name, flow.FlowId, stats.Total, stats.Pending, stats.Delivering, stats.Delivered, stats.Held,
            stats.Failed, stats.Deleted, stats.Drifted, stats.DeliveredLast24h, stats.LastDeliveredUtc, stats.LastVerifiedUtc, stats.Submissions,
            stats.LastSubmission is null ? null : ToDto(stats.LastSubmission)));
    }

    private static async Task<Results<Ok<PagedResult<DeliveryRecordDto>>, ProblemHttpResult>> ListRecordsAsync(
        Guid pipelineId, string? search, string? mode, string? status, Guid? submissionId, Guid? runId, bool? drifted, int? page, int? pageSize,
        CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(status, search, mode, submissionId, runId, drifted == true));
        if (query is null)
        {
            return invalid!;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var offset = (long)(p - 1) * size;
        if (offset >= RecordListing.CountLimit)
        {
            return TooBroad(
                $"A record listing pages through its first {RecordListing.CountLimit} records, and page {p} of {size} starts past them. " +
                "Narrow the filter (a status, a submission, a run or a search) to reach the records beyond.");
        }

        query = query with { Offset = (int)offset, Max = size };
        try
        {
            var items = await ledger.ListAsync(flow.FlowId, query, ct).ConfigureAwait(false);
            var total = await ledger.CountAsync(flow.FlowId, query, RecordListing.CountLimit + 1, ct).ConfigureAwait(false);
            return TypedResults.Ok(new PagedResult<DeliveryRecordDto>(
                items.Select(ToDto).ToList(), p, size, Math.Min(total.Count, RecordListing.CountLimit), TotalCapped: !total.Exact));
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }
    }

    /// <summary>The listing filter of a request, or the problem to answer with when it names something unknown.</summary>
    private static (RecordQuery? Query, ProblemHttpResult? Problem) BuildQuery(DeliveryRecordFilterDto filter)
    {
        RecordStatus? status = null;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (!Enum.TryParse<RecordStatus>(filter.Status, ignoreCase: true, out var parsed))
            {
                return (null, TypedResults.Problem(
                    detail: $"status must be one of {string.Join(", ", Enum.GetNames<RecordStatus>().Select(n => n.ToLowerInvariant()))}.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
            }

            status = parsed;
        }

        return (new RecordQuery
        {
            Status = status,
            Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
            Mode = string.Equals(filter.Mode, "contains", StringComparison.OrdinalIgnoreCase) ? SearchMode.Contains : SearchMode.Prefix,
            SubmissionId = filter.SubmissionId,
            RunId = filter.RunId,
            Drifted = filter.Drifted,
        }, null);
    }

    private static async Task<Results<Ok<DeliveryTargetDto>, ProblemHttpResult>> GetTargetAsync(
        Guid pipelineId, CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        return flow is null ? problem! : TypedResults.Ok(ToTargetDto(flow));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliverySubmissionDto>>, ProblemHttpResult>> ListSubmissionsAsync(
        Guid pipelineId, int? max, string? reference, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var submissions = await ledger.ListSubmissionsAsync(flow.FlowId, Math.Clamp(max ?? 100, 1, 1000), reference, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliverySubmissionDto>>(submissions.Select(ToDto).ToList());
    }

    /// <summary>A retrieval flow's runs, newest first. The flow id derives from the pipeline's name, as the executor derives it.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryRetrievalDto>>, ProblemHttpResult>> ListRetrievalsAsync(
        Guid pipelineId, int? max, CatalogDbContext db, ILedger ledger, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return NotFound("pipeline", pipelineId);
        }

        if (!string.Equals(pipeline.Kind, RetrievalDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                detail: $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a retrieval flow.",
                statusCode: StatusCodes.Status409Conflict, title: "Not a retrieval flow");
        }

        var rows = await ledger.ListRetrievalsAsync(FlowId.Of(pipeline.Name), Math.Clamp(max ?? 100, 1, 1000), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryRetrievalDto>>(rows.Select(r => new DeliveryRetrievalDto(
            r.RetrievalId, r.FlowId, r.FlowName, r.RunId, r.Actor, r.Kinds, r.Query, r.WindowField, r.WindowFrom, r.WindowTo, r.Location, r.ManifestLocation,
            r.Status, r.Records, r.Files, r.Bytes, r.StartedUtc, r.CompletedUtc, r.Error)).ToList());
    }

    private static async Task<Results<Ok<DeliveryRecordDetailDto>, ProblemHttpResult>> GetRecordAsync(
        Guid key, CatalogDbContext db, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.FindRecordAsync(new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound("record", key);
        }

        var pipeline = await FindPipelineAsync(db, record.FlowId, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryRecordDetailDto(ToDto(record), pipeline?.Id, pipeline?.RepoId, pipeline?.Name));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAttemptDto>>, ProblemHttpResult>> ListRecordAttemptsAsync(
        Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.FindRecordAsync(new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound("record", key);
        }

        var attempts = await ledger.ListAttemptsAsync(new DeliveryKey(key), Math.Clamp(max ?? 100, 1, MaxAttempts), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAttemptDto>>(attempts.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryActivityDto>>, ProblemHttpResult>> ListRecordActivitiesAsync(
        Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.FindRecordAsync(new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound("record", key);
        }

        var activities = await ledger.ListActivitiesAsync(new ActivityQuery { DeliveryKey = key, Max = Math.Clamp(max ?? 100, 1, MaxAttempts) }, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryActivityDto>>(activities.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliverySubmissionDetailDto>, ProblemHttpResult>> GetSubmissionAsync(
        Guid submissionId, CatalogDbContext db, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        var pipeline = await FindPipelineAsync(db, submission.FlowId, ct).ConfigureAwait(false);
        var runIds = await db.Runs.AsNoTracking()
            .Where(r => r.SubmissionId == submissionId || r.ResultSubmissionId == submissionId)
            .OrderByDescending(r => r.EnqueuedUtc)
            .Select(r => r.RunId)
            .Take(100)
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliverySubmissionDetailDto(ToDto(submission), pipeline?.Id, runIds));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAttemptDto>>, ProblemHttpResult>> ListSubmissionAttemptsAsync(
        Guid submissionId, int? max, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        var attempts = await ledger.ListAttemptsForSubmissionAsync(submissionId, Math.Clamp(max ?? 500, 1, 5000), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAttemptDto>>(attempts.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<PagedResult<DeliveryWorkBatchDto>>, ProblemHttpResult>> ListSubmissionBatchesAsync(
        Guid submissionId, string? status, int? page, int? pageSize, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        WorkBatchStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WorkBatchStatus>(status, ignoreCase: true, out var parsed))
            {
                return TypedResults.Problem(
                    detail: $"status must be one of {string.Join(", ", Enum.GetNames<WorkBatchStatus>().Select(n => n.ToLowerInvariant()))}.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            statusFilter = parsed;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var items = await ledger.ListWorkBatchesAsync(submissionId, size, (p - 1) * size, ct).ConfigureAwait(false);
        if (statusFilter is { } wanted)
        {
            items = items.Where(b => b.Status == wanted).ToList();
        }

        var total = await ledger.CountWorkBatchesAsync(submissionId, statusFilter, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryWorkBatchDto>(items.Select(ToDto).ToList(), p, size, (int)Math.Min(total, int.MaxValue)));
    }

    private static async Task<Results<Ok<PagedResult<DeliveryActivityDto>>, ProblemHttpResult>> ListActivitiesAsync(
        Guid? pipelineId, Guid? submissionId, Guid? runId, string? kind, string? actor, string? outcome, DateTime? since, DateTime? until,
        int? page, int? pageSize, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        Guid? flowId = null;
        if (pipelineId is { } pid)
        {
            var (flow, problem) = await ResolveAsync(db, documents, pid, ct).ConfigureAwait(false);
            if (flow is null)
            {
                return problem!;
            }

            flowId = flow.FlowId;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = new ActivityQuery
        {
            FlowId = flowId,
            SubmissionId = submissionId,
            RunId = runId,
            Kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant(),
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim().ToLowerInvariant(),
            SinceUtc = since is { } s ? DateTime.SpecifyKind(s.ToUniversalTime(), DateTimeKind.Utc) : null,
            UntilUtc = until is { } u ? DateTime.SpecifyKind(u.ToUniversalTime(), DateTimeKind.Utc) : null,
            Offset = (p - 1) * size,
            Max = size,
        };
        var items = await ledger.ListActivitiesAsync(query, ct).ConfigureAwait(false);
        var total = await ledger.CountActivitiesAsync(query, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryActivityDto>(items.Select(ToDto).ToList(), p, size, total));
    }

    private static async Task<Results<Ok<DeliveryActivityDto>, ProblemHttpResult>> GetActivityAsync(long activityId, ILedger ledger, CancellationToken ct)
    {
        var activity = await ledger.GetActivityAsync(activityId, ct).ConfigureAwait(false);
        return activity is null
            ? TypedResults.Problem(detail: $"No activity '{activityId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : TypedResults.Ok(ToDto(activity));
    }

    private static async Task<Ok<IReadOnlyList<DeliveryMappingDto>>> ListMappingsAsync(Guid? repoId, string? status, CatalogDbContext db, CancellationToken ct)
    {
        var query = db.DeliveryMappings.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(m => m.RepoId == r);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(m => m.Status == s);
        }

        var rows = await query.OrderBy(m => m.Name).ThenBy(m => m.Version).Take(1000).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryMappingDto>>(rows.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliveryMappingDetailDto>, ProblemHttpResult>> GetMappingAsync(Guid mappingId, CatalogDbContext db, CancellationToken ct)
    {
        var row = await db.DeliveryMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mappingId, ct).ConfigureAwait(false);
        return row is null ? NotFound("mapping", mappingId) : TypedResults.Ok(new DeliveryMappingDetailDto(ToDto(row), row.Yaml));
    }

    /// <summary>
    /// Every cache a synced cache flow declares: where it is defined (the repository and the file, which is where what is
    /// cached is changed), the flow's pipeline and the schedules that refresh it, each declared type with what the current
    /// version holds of it, and the current version with the run that captured it. Read-only: a cache is defined in its
    /// YAML and filled by its runs.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryCacheDto>>> ListCachesAsync(Guid? repoId, CatalogDbContext db, CancellationToken ct)
    {
        var query = db.DeliveryCacheDefinitions.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(c => c.RepoId == r);
        }

        var definitions = await query.OrderBy(c => c.CacheName).ThenBy(c => c.Name).Take(MaxCacheDefinitions).ToListAsync(ct).ConfigureAwait(false);
        if (definitions.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>([]);
        }

        var names = definitions.Select(d => d.CacheName).Distinct().ToList();
        var repoIds = definitions.Select(d => d.RepoId).Distinct().ToList();
        var repoNames = await RepoNamesAsync(db, repoIds, ct).ConfigureAwait(false);
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => repoIds.Contains(p.RepoId) && p.Kind == CacheDefinition.FlowTypeName && names.Contains(p.Name))
            .Select(p => new { p.Id, p.RepoId, p.Name, p.SourceServer })
            .ToListAsync(ct).ConfigureAwait(false);
        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var memberships = await db.ScheduleMembers.AsNoTracking()
            .Where(m => pipelineIds.Contains(m.PipelineId))
            .Select(m => new { m.PipelineId, m.ScheduleId })
            .ToListAsync(ct).ConfigureAwait(false);
        var scheduleIds = memberships.Select(m => m.ScheduleId).Distinct().ToList();
        var schedules = await db.Schedules.AsNoTracking()
            .Where(s => scheduleIds.Contains(s.Id))
            .Select(s => new DeliveryCacheScheduleDto(s.Id, s.Name, s.Cron, s.IntervalSeconds, s.Parents.Any()))
            .ToListAsync(ct).ConfigureAwait(false);
        var currentRows = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => names.Contains(v.CacheName) && v.Current)
            .ToListAsync(ct).ConfigureAwait(false);
        var versionCounts = await db.DeliveryCacheVersions.AsNoTracking()
            .Where(v => names.Contains(v.CacheName))
            .GroupBy(v => v.CacheName)
            .Select(g => new { Cache = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Cache, g => g.Count, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var caches = definitions
            .GroupBy(d => (d.RepoId, d.CacheName))
            .Select(group =>
            {
                var first = group.First();
                var pipeline = pipelines.FirstOrDefault(p => p.RepoId == first.RepoId && p.Name == first.CacheName);
                var current = currentRows.FirstOrDefault(v => v.CacheName == first.CacheName) is { } row ? CatalogCacheStore.Info(row) : null;
                var held = current?.Types.ToDictionary(t => t.Name, t => t.Items, StringComparer.Ordinal) ?? new Dictionary<string, long>(StringComparer.Ordinal);
                var refreshedBy = pipeline is null
                    ? []
                    : memberships
                        .Where(m => m.PipelineId == pipeline.Id)
                        .Join(schedules, m => m.ScheduleId, s => s.Id, (_, s) => s)
                        .OrderBy(s => s.Name, StringComparer.Ordinal)
                        .ToList();
                return new DeliveryCacheDto(
                    first.CacheName, first.RepoId, repoNames.GetValueOrDefault(first.RepoId, string.Empty), first.RelativePath, pipeline?.Id,
                    pipeline?.SourceServer, first.MakeCurrent,
                    group.Select(d => new DeliveryCacheTypeDto(d.Name, d.EntityType, d.Kind, d.Query, ParseJson(d.FieldsJson), d.OnChange, held.GetValueOrDefault(d.Name))).ToList(),
                    refreshedBy, current is null ? null : ToVersionDto(current), versionCounts.GetValueOrDefault(first.CacheName));
            })
            .ToList();
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>(caches);
    }

    /// <summary>
    /// The cached records of one cache, filtered by type and searched over every value they hold, so an operator can answer
    /// "is this unit cached, and under which id". The listing reads exactly one version: <paramref name="version"/> names it,
    /// and without one it is the cache's current version, the one delivery flows render against.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryCachedItemDto>>, ProblemHttpResult>> ListCachedItemsAsync(
        string? cache, string? type, string? search, string? version, int? page, int? pageSize, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cache))
        {
            return NoCacheNamed();
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var name = cache.Trim();
        var resolved = await CacheVersions.ResolveAsync(db, name, version, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return string.IsNullOrWhiteSpace(version)
                ? TypedResults.Ok(new PagedResult<DeliveryCachedItemDto>([], p, size, 0))
                : UnknownCacheVersion(name, version.Trim());
        }

        var query = CacheVersions.ItemsAt(db, name, resolved.Sequence);
        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim();
            query = query.Where(i => i.TypeName == t);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(i => i.RecordId.Contains(term) || i.Terms.Contains(term));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderBy(i => i.TypeName).ThenBy(i => i.RecordId)
            .Skip((p - 1) * size).Take(size)
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryCachedItemDto>(
            items.Select(i => new DeliveryCachedItemDto(i.ItemId, name, resolved.Version, i.TypeName, i.EntityType, i.RecordId, ParseJson(i.FieldsJson))).ToList(),
            p, size, total));
    }

    /// <summary>The versions of one cache, newest first, each with the run that captured it: what the version picker offers.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheVersionDto>>, ProblemHttpResult>> ListCacheVersionsAsync(
        string? cache, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cache))
        {
            return NoCacheNamed();
        }

        var versions = await CacheVersions.ListAsync(db, cache.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheVersionDto>>(versions.Select(ToVersionDto).ToList());
    }

    /// <summary>
    /// One cache's history, newest first: each version with the version captured before it and how many records it changed,
    /// added and removed. Naming a <paramref name="type"/> narrows the counts to that type, so the versions that changed it
    /// can be told apart.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>, ProblemHttpResult>> ListCacheHistoryAsync(
        string? cache, string? type, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cache))
        {
            return NoCacheNamed();
        }

        var history = await CacheVersions.HistoryAsync(db, cache.Trim(), string.IsNullOrWhiteSpace(type) ? null : type.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>(history
            .Select(h => new DeliveryCacheHistoryEntryDto(ToVersionDto(h.Version), h.Before, h.Changes.Changed, h.Changes.Added, h.Changes.Removed))
            .ToList());
    }

    private static DeliveryCacheVersionDto ToVersionDto(CacheVersionInfo version)
        => new(
            version.CacheName, version.Version, version.Sequence, version.CapturedUtc, version.Current, version.PreviousVersion, version.RunId,
            version.CapturedBy, version.Origin, version.Items, version.Types.Select(t => new DeliveryCacheVersionTypeDto(t.Name, t.EntityType, t.Items)).ToList());

    private static ProblemHttpResult NoCacheNamed()
        => TypedResults.Problem(title: "No cache", detail: "Name the cache to read with 'cache': the name of its cache flow.", statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult UnknownCacheVersion(string cache, string version)
        => TypedResults.Problem(title: "Unknown version", detail: $"Cache '{cache}' holds no version '{version}'.", statusCode: StatusCodes.Status404NotFound);

    /// <summary>The names of the repositories a cache answer mentions, by id.</summary>
    private static async Task<Dictionary<Guid, string>> RepoNamesAsync(CatalogDbContext db, IEnumerable<Guid> repoIds, CancellationToken ct)
    {
        var ids = repoIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Repos.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What changed in one cache between two versions: per type, the records whose captured values moved, the records the
    /// later version added and the ones it no longer holds, a page at a time. <paramref name="to"/> defaults to the cache's
    /// current version.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCacheDiffDto>, ProblemHttpResult>> CompareCacheVersionsAsync(
        string? cache, string? from, string? to, string? type, string? change, string? search, int? page, int? pageSize,
        CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cache))
        {
            return NoCacheNamed();
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return TypedResults.Problem(
                title: "No version", detail: "Name the earlier cache version to compare with 'from'.", statusCode: StatusCodes.Status400BadRequest);
        }

        CacheItemChange? kind = null;
        if (!string.IsNullOrWhiteSpace(change))
        {
            var text = change.Trim();
            if (!text.All(char.IsLetter) || !Enum.TryParse<CacheItemChange>(text, ignoreCase: true, out var parsed))
            {
                return TypedResults.Problem(
                    title: "Unknown change", detail: $"'change' is changed, added or removed, not '{text}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            kind = parsed;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = new CacheComparisonQuery(cache.Trim(), from.Trim())
        {
            ToVersion = string.IsNullOrWhiteSpace(to) ? null : to.Trim(),
            Type = string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
            Change = kind,
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Skip = (int)Math.Min(int.MaxValue, (long)(p - 1) * size),
            Take = size,
        };
        var diff = await CacheVersions.CompareAsync(db, query, ct).ConfigureAwait(false);
        if (diff is null)
        {
            var named = query.ToVersion is null ? $"'{query.FromVersion}' (or has no current version)" : $"'{query.FromVersion}' or '{query.ToVersion}'";
            return TypedResults.Problem(
                title: "Unknown version", detail: $"Cache '{query.Cache}' holds no version {named}.", statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(new DeliveryCacheDiffDto(
            diff.Cache, diff.FromVersion, diff.ToVersion, diff.Changed, diff.Added, diff.Removed,
            diff.Types.Select(t => new DeliveryCacheDiffTypeDto(t.TypeName, t.Changed, t.Added, t.Removed)).ToList(),
            new PagedResult<DeliveryCacheDiffItemDto>(
                diff.Items.Select(i => new DeliveryCacheDiffItemDto(
                    i.TypeName, i.EntityType, i.RecordId, i.Change.ToString().ToLowerInvariant(),
                    i.BeforeJson is null ? null : ParseJson(i.BeforeJson),
                    i.AfterJson is null ? null : ParseJson(i.AfterJson),
                    i.ChangedFields)).ToList(),
                p, size, diff.Total)));
    }

    /// <summary>
    /// The cache changes delivered records were built from: one row per change with what it reaches, filtered by
    /// status (pending, approved, rolling, rejected, applied) and, with <c>cache</c>, narrowed to the changes one cache's
    /// refreshes found.
    /// </summary>
    private static async Task<Ok<PagedResult<DeliveryUpdateTagDto>>> ListUpdateTagsAsync(
        string? status, string? cache, int? page, int? pageSize, ILedger ledger, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var tags = await ledger.ListTagsAsync(status, size, (p - 1) * size, cache, ct).ConfigureAwait(false);
        var total = await ledger.CountTagsAsync(status, cache, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryUpdateTagDto>(tags.Select(ToDto).ToList(), p, size, total));
    }

    /// <summary>Approves or rejects tags. Approving releases the records so the next run carries the new document.</summary>
    private static async Task<Results<Ok<DeliveryTagDecisionResult>, ProblemHttpResult>> DecideUpdateTagsAsync(
        DeliveryTagDecisionRequest request, ILedger ledger, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null || request.TagIds.Count == 0)
        {
            return TypedResults.Problem(title: "No tags", detail: "Name at least one tag to decide.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.TagIds.Count > 1000)
        {
            return TypedResults.Problem(title: "Too many tags", detail: "At most 1000 tags can be decided in one call.", statusCode: StatusCodes.Status400BadRequest);
        }

        var decided = await ledger.DecideTagsAsync(request.TagIds, request.Approve, RequestActor.Of(user) ?? "unknown", clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryTagDecisionResult(decided, request.Approve));
    }

    /// <summary>What one record read out of the cache when it was rendered, through the set it shares.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheUseDto>>, ProblemHttpResult>> ListRecordCacheUsesAsync(
        Guid key, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.FindRecordAsync(new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound("record", key);
        }

        if (record.CacheSetId is not { } setId)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>([]);
        }

        var uses = await ledger.ListCacheSetAsync(setId, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>(uses
            .Select(u => new DeliveryCacheUseDto(u.CacheName, u.TypeName, u.ItemId, u.Path, u.Kind.ToString().ToLowerInvariant(), u.ValueText))
            .ToList());
    }

    // ---- Interventions -------------------------------------------------------------------------------------------

    private static async Task<Results<Accepted<DeliverySubmissionAccepted>, Ok<DeliverySubmissionAccepted>, ProblemHttpResult>> SubmitAsync(
        DeliverySubmissionRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null)
        {
            return InvalidSubmission("A submission names its flow, and either the drop to deliver (drop) or the records to deliver (records).");
        }

        var hasDrop = !string.IsNullOrWhiteSpace(request.Drop);
        var hasRecords = request.Records is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) };
        if (hasDrop == hasRecords)
        {
            return InvalidSubmission(hasDrop
                ? "A submission names a drop or carries records, not both."
                : "A submission names the drop to deliver (drop) or carries the records to deliver (records).");
        }

        var operation = string.IsNullOrWhiteSpace(request.Operation) ? RunParameters.DeliverOperation : request.Operation.Trim().ToLowerInvariant();
        if (!InlineSubmissionState.Operations.Contains(operation, StringComparer.Ordinal))
        {
            return InvalidSubmission($"operation is {string.Join(" or ", InlineSubmissionState.Operations)}.");
        }

        if (hasDrop && request.SubmissionId is not null)
        {
            return InvalidSubmission("A drop's submission id is the one its manifest carries; submissionId goes with records.");
        }

        if (hasDrop && request.Reference is not null)
        {
            return InvalidSubmission(
                "A drop's reference is the one its manifest carries, as its submission id is. Put 'reference' in the manifest the preparing side writes; on a submission it goes with records.");
        }

        if (SubmissionReference.Refusal(request.Reference) is { } referenceRefusal)
        {
            return InvalidSubmission(referenceRefusal);
        }

        var (flow, problem) = await ResolveSubmissionFlowAsync(db, documents, request, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (hasRecords)
        {
            return await SubmitRecordsAsync(request, request.Records!.Value, operation, flow, db, ledger, dispatcher, clock, user, ct).ConfigureAwait(false);
        }

        var parameters = new RunParameters
        {
            Operation = operation,
            Force = request.Force,
            Drop = request.Drop!.Trim(),
            Values = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
        try
        {
            parameters.Validate();
        }
        catch (SqlFlowException ex)
        {
            return InvalidSubmission(ex.Message, "Invalid run parameters");
        }

        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliverySubmissionAccepted(runId, flow.Pipeline.Id, flow.Pipeline.Name, RunStatuses.Queued));
    }

    /// <summary>
    /// Inline records (design.md section 3.4). The records are checked, and stored in the same transaction as the run that
    /// takes them, under the submission id the caller chose or a new one. The same request again answers with the run it
    /// started and queues nothing; the same id with anything different is a conflict, since the id names one request.
    /// </summary>
    private static async Task<Results<Accepted<DeliverySubmissionAccepted>, Ok<DeliverySubmissionAccepted>, ProblemHttpResult>> SubmitRecordsAsync(
        DeliverySubmissionRequest request, JsonElement recordsElement, string operation, FlowContext flow, CatalogDbContext db, ILedger ledger,
        IRunDispatcher dispatcher, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (InlineDrop.Refusal(flow.Flow) is { } refusal)
        {
            return InvalidSubmission(refusal, "Records not accepted by this flow");
        }

        if (request.SubmissionId == Guid.Empty)
        {
            return InvalidSubmission("submissionId must be a non-empty UUID.");
        }

        IReadOnlyDictionary<string, string> values;
        try
        {
            new RunParameters { Values = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal) }.Validate();
            values = FlowParameters.Resolve(flow.Flow, request.Parameters);
        }
        catch (Exception ex) when (ex is SqlFlowException or FlowValidationException)
        {
            return InvalidSubmission(ex.Message, "Invalid run parameters");
        }

        InlineRecords records;
        try
        {
            records = InlineRecords.Parse(recordsElement);
        }
        catch (FlowValidationException ex)
        {
            return InvalidSubmission(ex.Message, "Invalid records");
        }

        // Where the records say their files are is checked before anything is stored: the node opens those locations with
        // its own identity when the run delivers, so the flow's roots are what keeps a submission from having any file
        // that identity can read shipped to OSDU (design.md section 3.4).
        if (InlineDrop.FilesRefusal(flow.Flow, records) is { } filesRefusal)
        {
            return InvalidSubmission(filesRefusal, "Invalid records");
        }

        var submissionId = request.SubmissionId ?? Guid.CreateVersion7();
        var accepted = InlineSubmissionState.Accept(
            submissionId, flow.Flow, operation, request.Force, values, records, clock.GetUtcNow().UtcDateTime, RequestActor.Label(user), request.Reference);
        var stored = await ledger.GetInlineSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (stored is null && await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false) is { } dropSubmission)
        {
            return SubmissionConflict($"Submission {submissionId:D} is a drop submission of flow '{dropSubmission.FlowName}'; records are sent under an id of their own.");
        }

        for (var attempt = 1; stored is null; attempt++)
        {
            try
            {
                var runId = await EnqueueRunAsync(db, dispatcher, flow, InlineRunParameters(accepted, values), request.Pool, user, [InlineSubmissionRows.ToEntity(accepted)], ct).ConfigureAwait(false);
                return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliverySubmissionAccepted(runId, flow.Pipeline.Id, flow.Pipeline.Name, RunStatuses.Queued, submissionId));
            }
            catch (DbUpdateException) when (attempt < MaxAcceptAttempts)
            {
                // The insert lost to another request for the same id: a duplicate key, or a deadlock between the two
                // serializable transactions. Rows and run commit together, so what that request stored decides whether
                // this one is a repeat or a conflict; while nothing is stored yet, the insert is tried again. The failed
                // inserts are dropped from this context first, or its next save would try them again.
                db.ChangeTracker.Clear();
                stored = await ledger.GetInlineSubmissionAsync(submissionId, ct).ConfigureAwait(false);
            }
        }

        var differences = accepted.Differences(stored);
        if (differences.Count > 0)
        {
            return SubmissionConflict(
                $"Submission {submissionId:D} was accepted at {stored.ReceivedUtc:yyyy-MM-dd'T'HH:mm:ss'Z'} from {stored.ReceivedBy}, and this request differs from it in " +
                $"{string.Join(", ", differences)}. A submission id names one request: send changed records under a new id.");
        }

        var run = await db.Runs.AsNoTracking()
            .Where(r => r.SubmissionId == submissionId)
            .OrderBy(r => r.EnqueuedUtc)
            .Select(r => new { r.RunId, r.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is not null)
        {
            return TypedResults.Ok(new DeliverySubmissionAccepted(run.RunId, flow.Pipeline.Id, flow.Pipeline.Name, run.Status, submissionId, Replayed: true));
        }

        // The run that took it is no longer in the catalog (its row was removed): a new run takes the stored submission.
        var again = await EnqueueRunAsync(db, dispatcher, flow, InlineRunParameters(stored, stored.Parameters()), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{again}", new DeliverySubmissionAccepted(again, flow.Pipeline.Id, flow.Pipeline.Name, RunStatuses.Queued, submissionId, Replayed: true));
    }

    private static RunParameters InlineRunParameters(InlineSubmissionState submission, IReadOnlyDictionary<string, string> values) => new()
    {
        Operation = submission.Operation,
        Force = submission.Force,
        SubmissionId = submission.SubmissionId,
        Values = values,
    };

    private static ProblemHttpResult InvalidSubmission(string detail, string title = "Invalid request")
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);

    private static ProblemHttpResult SubmissionConflict(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Submission id already used");

    /// <summary>
    /// The flows records can be submitted to by hand: every active delivery flow whose document offers manual
    /// submission, with the template version its mapping fills. With <c>all</c> the flows that do not are listed too, each
    /// with the reason, so an operator can see why a flow is not on the list. A flow whose document no longer parses is
    /// left out of both.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryManualFlowDto>>> ListManualSubmissionFlowsAsync(
        bool? all, CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active && p.Kind == FlowDefinition.FlowTypeName)
            .OrderBy(p => p.Name)
            .Take(MaxManualSubmissionFlows)
            .ToListAsync(ct).ConfigureAwait(false);

        // The template version each synced mapping of these repositories fills, so the listing says what a flow's records become.
        var repoIds = pipelines.Select(p => p.RepoId).Distinct().ToList();
        var synced = await db.DeliveryMappings.AsNoTracking()
            .Where(m => repoIds.Contains(m.RepoId) && m.Status == "valid")
            .Select(m => new { m.RepoId, m.Reference, m.Kind, m.TemplateVersion })
            .ToListAsync(ct).ConfigureAwait(false);
        var pins = new Dictionary<string, TemplateReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in synced)
        {
            pins.TryAdd(PinKey(mapping.RepoId, mapping.Reference), new TemplateReference(mapping.Kind, mapping.TemplateVersion));
        }

        var flows = new List<DeliveryManualFlowDto>(pipelines.Count);
        foreach (var pipeline in pipelines)
        {
            FlowDefinition flow;
            try
            {
                flow = documents.ParseFlow(pipeline.Yaml, pipeline.RelativePath);
            }
            catch (FlowValidationException)
            {
                // The catalog's copy does not parse; the flow's own page reports that, and this listing stays honest
                // by leaving out a flow whose document cannot be read at all.
                continue;
            }

            var refusal = InlineDrop.Refusal(flow);
            if (refusal is not null && all != true)
            {
                continue;
            }

            TemplateReference? pin = pins.TryGetValue(PinKey(pipeline.RepoId, flow.Render.Mapping), out var found) ? found : null;
            flows.Add(new DeliveryManualFlowDto(
                pipeline.Id, pipeline.RepoId, pipeline.Name, pipeline.Batch, flow.Render.Mapping, flow.Target.Protocol.ToString(),
                refusal is null, refusal,
                flow.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new DeliveryFlowParameterDto(kv.Key, kv.Value.Required, kv.Value.Default, kv.Value.Description))
                    .ToList(),
                InlineDrop.PayloadContract(flow).PayloadName,
                pin?.Kind,
                pin?.Version));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryManualFlowDto>>(flows);
    }

    /// <summary>The key a synced mapping is found under for a flow: its repository and its reference.</summary>
    private static string PinKey(Guid repoId, string reference) => repoId.ToString("N") + "/" + reference;

    /// <summary>
    /// What a source sends the flow: its parameters, the template version its pinned mapping fills, the dataset key, every
    /// column the mapping reads with the template variables each fills, and whether the flow takes records inline.
    /// </summary>
    private static async Task<Results<Ok<DeliverySourceContractDto>, ProblemHttpResult>> GetSourceContractAsync(
        Guid pipelineId, CatalogDbContext db, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var definition = flow.Flow;
        var reference = definition.Render.Mapping;
        var row = await db.DeliveryMappings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.RepoId == flow.Pipeline.RepoId && m.Reference == reference, ct).ConfigureAwait(false);
        MappingDefinition? mapping = null;
        string? mappingProblem = null;
        if (row is null)
        {
            mappingProblem = $"The flow pins mapping '{reference}', which the catalog has not synced from the flow's repository. Re-sync the repository.";
        }
        else if (!string.Equals(row.Status, "valid", StringComparison.OrdinalIgnoreCase))
        {
            mappingProblem = $"Mapping '{reference}' is invalid in the catalog: {row.Message}";
        }
        else
        {
            try
            {
                mapping = documents.ParseMapping(row.Yaml, row.RelativePath);
            }
            catch (FlowValidationException ex)
            {
                mappingProblem = $"Mapping '{reference}' does not parse: {ex.Message}";
            }
        }

        DeliverySourceTemplateDto? template = null;
        MappingSourceColumns? columns = null;
        if (mapping is not null)
        {
            var kind = mapping.Template.Kind;
            var version = mapping.Template.Version;
            var saved = await db.DeliveryTemplates.AsNoTracking().AnyAsync(t => t.Kind == kind && t.Version == version, ct).ConfigureAwait(false);
            template = new DeliverySourceTemplateDto(kind, version, saved);
            columns = MappingColumns.Read(mapping);
            if (!saved)
            {
                mappingProblem = $"Mapping '{reference}' pins template {mapping.Template}, which is not saved in the catalog, so a run cannot render the records. Save it on the Templates page, or with 'sqlflow template import'.";
            }
        }

        var refusal = InlineDrop.Refusal(definition);
        var payload = InlineDrop.PayloadContract(definition);
        return TypedResults.Ok(new DeliverySourceContractDto(
            flow.Pipeline.Id, flow.Pipeline.Name, reference, definition.Target.Protocol.ToString(), refusal is null, refusal,
            definition.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new DeliveryFlowParameterDto(kv.Key, kv.Value.Required, kv.Value.Default, kv.Value.Description)).ToList(),
            template, mapping?.Dataset.System, mapping?.Dataset.Key ?? [], mapping?.Dataset.Label,
            columns?.Record.Select(ToColumnDto).ToList() ?? [],
            columns?.Datasets.Select(d => new DeliverySourceDatasetDto(
                d.Name,
                d.Repeaters.Select(r => new DeliverySourceRepeaterDto(r.Target.Text, r.Required)).ToList(),
                d.Columns.Select(ToColumnDto).ToList())).ToList() ?? [],
            definition.Source.LastModified, definition.Source.Fingerprint, mappingProblem,
            InlineRecords.MaxRecords, InlineRecords.MaxChildRows, InlineRecords.MaxContentBytes,
            payload.PayloadName, payload.HashRequired, payload.Roots));
    }

    /// <summary>A column of the source contract, with every entry that reads it in the mapping's own notation.</summary>
    private static DeliverySourceColumnDto ToColumnDto(MappingColumn column) => new(
        column.Name,
        column.Key,
        column.Label,
        column.Uses.Select(use => new DeliverySourceColumnUseDto(
            use.Entry.Target.Text,
            use.Role switch
            {
                ColumnRole.Value => "value",
                ColumnRole.FindBy => "findBy",
                _ => "appliesWhen",
            },
            use.Entry.Source?.ToString(),
            use.Entry.Required,
            use.Role == ColumnRole.AppliesWhen ? [] : use.Entry.Modifiers.Select(m => m.ToString()).ToList(),
            use.Find?.ToString(),
            use.Entry.AppliesWhen?.ToString())).ToList());

    /// <summary>The records an inline submission carried, as the ledger holds them.</summary>
    private static async Task<Results<Ok<DeliveryInlineSubmissionDto>, ProblemHttpResult>> GetSubmissionContentAsync(
        Guid submissionId, CatalogDbContext db, ILedger ledger, CancellationToken ct)
    {
        var inline = await ledger.GetInlineSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (inline is null)
        {
            return TypedResults.Problem(
                detail: $"Submission {submissionId:D} carries no inline records: it is a drop submission, or no submission has that id.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var pipeline = await FindPipelineAsync(db, inline.FlowId, ct).ConfigureAwait(false);
        var runIds = await db.Runs.AsNoTracking()
            .Where(r => r.SubmissionId == submissionId)
            .OrderByDescending(r => r.EnqueuedUtc)
            .Select(r => r.RunId)
            .Take(100)
            .ToListAsync(ct).ConfigureAwait(false);
        using var records = JsonDocument.Parse(inline.RecordsJson);
        return TypedResults.Ok(new DeliveryInlineSubmissionDto(
            inline.SubmissionId, inline.FlowId, inline.FlowName, pipeline?.Id, inline.MappingReference, inline.Operation, inline.Force, inline.ParametersJson,
            inline.RecordCount, inline.ChildRowCount, inline.ContentBytes, inline.ContentHash, inline.ReceivedUtc, inline.ReceivedBy, inline.DropLocation,
            inline.WrittenUtc, runIds, records.RootElement.Clone(), inline.Reference));
    }

    private static async Task<Results<Ok<DeliveryReleaseResult>, ProblemHttpResult>> ReleaseFlowAsync(
        Guid pipelineId, DeliveryReleaseRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents, EngineContext engine,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var keys = request?.Keys is { Count: > 0 } k ? k.Select(g => new DeliveryKey(g)).ToList() : null;
        using var runtime = FlowRuntime.ForTarget(engine, flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var released = await runtime.ReleaseAsync(keys, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryReleaseResult(released));
    }

    private static async Task<Results<Ok<DeliveryReleaseResult>, ProblemHttpResult>> ReleaseRecordAsync(
        Guid key, CatalogDbContext db, DeliveryDocumentLoader documents, EngineContext engine, ILedger ledger, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveForRecordAsync(db, documents, ledger, key, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        using var runtime = FlowRuntime.ForTarget(engine, flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var released = await runtime.ReleaseAsync([new DeliveryKey(key)], ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryReleaseResult(released));
    }

    private static async Task<Results<Ok<DeliveryRedeliverResult>, Accepted<DeliveryRedeliverResult>, ProblemHttpResult>> RedeliverAsync(
        Guid key, DeliveryRedeliverRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents, EngineContext engine, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveForRecordAsync(db, documents, ledger, key, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var scopeText = string.IsNullOrWhiteSpace(request?.Scope) ? "all" : request.Scope.Trim();
        if (!Enum.TryParse<RedeliverScope>(scopeText, ignoreCase: true, out var scope))
        {
            return TypedResults.Problem(detail: "scope must be all, metadata or payload.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // With a run, the node marks and re-sends in one recorded run (the mark carries the run id and the scope);
        // without one, the mark is made here under the caller's name and the next delivery of the drop sends the
        // record. The run works on the record's last submission: that drop holds the record's source row, and the
        // submission carries the flow parameter values it was opened with, which a flow declaring a required parameter
        // cannot run without.
        if (request?.Run ?? true)
        {
            var record = await ledger.FindRecordAsync(new DeliveryKey(key), ct).ConfigureAwait(false);
            var parameters = new RunParameters
            {
                RecordKeys = [key],
                Redeliver = scope.ToString().ToLowerInvariant(),
                SubmissionId = record?.LastSubmissionId,
            };
            var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRedeliverResult(1, runId));
        }

        using var runtime = FlowRuntime.ForTarget(engine, flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var marked = await runtime.RedeliverAsync([new DeliveryKey(key)], scope, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryRedeliverResult(marked, null));
    }

    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> VerifyRecordAsync(
        Guid key, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveForRecordAsync(db, documents, ledger, key, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var parameters = new RunParameters { Operation = RunParameters.VerifyOperation, Force = true, RecordKeys = [key] };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> ReadRecordAsync(
        Guid key, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveForRecordAsync(db, documents, ledger, key, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        return await EnqueueOperationAsync(db, dispatcher, flow, ReadRecordOperation.OperationName,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["deliveryKey"] = key.ToString("D") }, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One record's removal. It is the many-record path with a selection of one, so a single delete and a bulk
    /// delete are the same operation, the same ledger writes and the same result shape.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> DeleteRecordAsync(
        Guid key, DeliveryRemovalRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveForRecordAsync(db, documents, ledger, key, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (!RemovalScopes.TryParse(request?.Scope, out var scope))
        {
            return BadScope(request?.Scope);
        }

        return await EnqueueRemovalAsync(db, dispatcher, flow, scope, KeyArguments([key]), 1, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A removal of the records an operator selected, or of every record their listing matches. The filter form
    /// resolves on the node at the moment the removal runs; what is checked here is that the count the operator was
    /// shown is still the count the filter yields, so a set that changed underneath them stops the removal instead
    /// of quietly widening it.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> RemoveRecordsAsync(
        Guid pipelineId, DeliveryRemovalRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (request is null || !RemovalScopes.TryParse(request.Scope, out var scope))
        {
            return BadScope(request?.Scope);
        }

        if (request.Keys is { Count: > 0 })
        {
            if (request.Filter is not null)
            {
                return TypedResults.Problem(
                    detail: "A removal names its records either by keys or by filter, not both.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            if (request.Keys.Count > RemovalLimits.MaxSelection)
            {
                return TypedResults.Problem(
                    detail: $"A removal takes at most {RemovalLimits.MaxSelection} records at a time; {request.Keys.Count} were selected.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Too many records");
            }

            return await EnqueueRemovalAsync(db, dispatcher, flow, scope, KeyArguments(request.Keys), request.Keys.Count, user, ct).ConfigureAwait(false);
        }

        if (request.Filter is null)
        {
            return TypedResults.Problem(
                detail: "A removal needs either keys or a filter; the request carries neither.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var (query, invalid) = BuildQuery(request.Filter);
        if (query is null)
        {
            return invalid!;
        }

        BoundedCount matched;
        try
        {
            matched = await ledger.CountAsync(flow.FlowId, query, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }

        if (!matched.Exact || matched.Count > RemovalLimits.MaxSelection)
        {
            return TypedResults.Problem(
                detail: $"The filter matches more than {RemovalLimits.MaxSelection} records as far as the listing counts; a removal takes at most {RemovalLimits.MaxSelection} at a time. Narrow the filter and remove in parts.",
                statusCode: StatusCodes.Status409Conflict, title: "Too many records");
        }

        if (matched.Count == 0)
        {
            return TypedResults.Problem(
                detail: "The filter matches no records, so there is nothing to remove.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing selected");
        }

        if (request.Expected is { } expected && expected != matched.Count)
        {
            return TypedResults.Problem(
                detail: $"The filter matched {expected} records when it was shown and matches {matched.Count} now. Nothing was removed; check the list and confirm again.",
                statusCode: StatusCodes.Status409Conflict, title: "The selection changed");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["filter"] = RemovalFilter.ToJson(query) };
        return await EnqueueRemovalAsync(db, dispatcher, flow, scope, arguments, matched.Count, user, ct).ConfigureAwait(false);
    }

    /// <summary>What a removal would take away, and from where: the confirmation's contents, computed not guessed.</summary>
    private static async Task<Results<Ok<DeliveryRemovalPreview>, ProblemHttpResult>> PreviewRemovalAsync(
        Guid pipelineId, DeliveryRemovalRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (request is null || !RemovalScopes.TryParse(request.Scope, out var scope))
        {
            return BadScope(request?.Scope);
        }

        // A record is given its OSDU id when it is planned, so an id proves nothing about what OSDU holds. What the
        // operator needs to know is how many of the selection were ever actually delivered.
        int records;
        int neverDelivered;
        bool capped;
        if (request.Keys is { Count: > 0 })
        {
            var found = await ledger.GetRecordsAsync(flow.FlowId, request.Keys.Select(k => new DeliveryKey(k)), ct).ConfigureAwait(false);
            records = request.Keys.Count;
            neverDelivered = records - found.Values.Count(r => r.LastDeliveredUtc is not null);
            capped = records > RemovalLimits.MaxSelection;
        }
        else
        {
            if (request.Filter is null)
            {
                return TypedResults.Problem(
                    detail: "A removal preview needs either keys or a filter; the request carries neither.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            var (query, invalid) = BuildQuery(request.Filter);
            if (query is null)
            {
                return invalid!;
            }

            try
            {
                // Counted as far as one removal takes and one more, so a selection larger than a removal is named as such.
                var counted = await ledger.CountAsync(flow.FlowId, query, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
                var never = await ledger.CountAsync(flow.FlowId, query with { EverDelivered = false }, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
                capped = !counted.Exact || counted.Count > RemovalLimits.MaxSelection;
                records = Math.Min(counted.Count, RemovalLimits.MaxSelection);
                neverDelivered = Math.Min(never.Count, records);
            }
            catch (RecordQueryTooBroadException ex)
            {
                return TooBroad(ex.Message);
            }
        }

        return TypedResults.Ok(new DeliveryRemovalPreview(
            RemovalScopes.Wire(scope), records, Math.Max(0, records - neverDelivered), neverDelivered, capped, ToTargetDto(flow)));
    }

    /// <summary>A record listing outside the ledger's bounds (<see cref="RecordListing"/>): the detail says how to narrow it.</summary>
    private static ProblemHttpResult TooBroad(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Listing too broad");

    private static ProblemHttpResult BadScope(string? scope)
        => TypedResults.Problem(
            detail: $"'{scope ?? "(none)"}' is not a removal scope. Use record (reversible), history (earlier versions only) or everything (the record and every version).",
            statusCode: StatusCodes.Status400BadRequest, title: "Invalid removal scope");

    private static Dictionary<string, string> KeyArguments(IReadOnlyList<Guid> keys)
        => new(StringComparer.Ordinal) { ["deliveryKeys"] = string.Join(',', keys.Select(k => k.ToString("D"))) };

    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> EnqueueRemovalAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, RemovalScope scope, Dictionary<string, string> arguments,
        int records, ClaimsPrincipal user, CancellationToken ct)
    {
        arguments["scope"] = RemovalScopes.Wire(scope);
        var queued = await EnqueueOperationAsync(db, dispatcher, flow, DeleteRecordOperation.OperationName, arguments, user, ct).ConfigureAwait(false);
        if (queued.Result is not Accepted<ComputeTaskAccepted> accepted || accepted.Value is null)
        {
            return (ProblemHttpResult)queued.Result;
        }

        return TypedResults.Accepted(
            accepted.Location,
            new DeliveryRemovalAccepted(accepted.Value.TaskId, accepted.Value.Status, RemovalScopes.Wire(scope), records));
    }

    /// <summary>
    /// The flow's target as the GUI names it before a removal. The data partition is read from the flow's headers,
    /// which is where OSDU takes it; no other header is reported, since a header can carry a credential reference.
    /// </summary>
    private static DeliveryTargetDto ToTargetDto(FlowContext flow)
    {
        var target = flow.Flow.Target;
        target.Headers.TryGetValue("data-partition-id", out var partition);
        var paths = RemovalEndpoints.Of(target);
        return new DeliveryTargetDto(
            flow.Pipeline.Id, flow.Pipeline.Name, target.Endpoint, partition, target.Protocol.ToString(),
            target.Auth.Type.ToString(), paths.Record, paths.History, paths.Everything);
    }

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> ProbeAsync(
        Guid pipelineId, CatalogDbContext db, DeliveryDocumentLoader documents, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        return await EnqueueOperationAsync(db, dispatcher, flow, ProbeTargetOperation.OperationName, new Dictionary<string, string>(StringComparer.Ordinal), user, ct).ConfigureAwait(false);
    }

    private static async Task<Results<Ok<DeliveryPruneResult>, ProblemHttpResult>> PruneAsync(
        DeliveryPruneRequest request, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || request.OlderThanDays < 1)
        {
            return TypedResults.Problem(detail: "olderThanDays must be at least 1.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var pruned = await ledger.PruneAttemptsAsync(clock.GetUtcNow().UtcDateTime.AddDays(-request.OlderThanDays), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryPruneResult(pruned));
    }

    // ---- Plumbing ------------------------------------------------------------------------------------------------

    internal sealed record FlowContext(CatalogPipeline Pipeline, FlowDefinition Flow)
    {
        public Guid FlowId => Flow.Id;
    }

    /// <summary>The delivery pipeline and its parsed flow, or the problem to answer with.</summary>
    internal static async Task<(FlowContext? Flow, ProblemHttpResult? Problem)> ResolveAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, Guid pipelineId, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return (null, NotFound("pipeline", pipelineId));
        }

        return Parse(documents, pipeline);
    }

    internal static (FlowContext? Flow, ProblemHttpResult? Problem) Parse(DeliveryDocumentLoader documents, CatalogPipeline pipeline)
    {
        if (!string.Equals(pipeline.Kind, FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, TypedResults.Problem(
                detail: $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a delivery flow.",
                statusCode: StatusCodes.Status409Conflict, title: "Not a delivery flow"));
        }

        try
        {
            return (new FlowContext(pipeline, documents.ParseFlow(pipeline.Yaml, pipeline.RelativePath)), null);
        }
        catch (FlowValidationException ex)
        {
            return (null, TypedResults.Problem(
                detail: $"The catalog's copy of '{pipeline.Name}' does not parse: {ex.Message} Re-sync the repository.",
                statusCode: StatusCodes.Status409Conflict, title: "Flow document invalid"));
        }
    }

    /// <summary>The pipeline behind a ledger record: the delivery flow whose name yields the record's flow id.</summary>
    private static async Task<(FlowContext? Flow, ProblemHttpResult? Problem)> ResolveForRecordAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, Guid key, CancellationToken ct)
    {
        var record = await ledger.FindRecordAsync(new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return (null, NotFound("record", key));
        }

        var pipeline = await FindPipelineAsync(db, record.FlowId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return (null, TypedResults.Problem(
                detail: "The record's flow is no longer in any synced repository, so nothing can act on it. Restore the flow document and sync.",
                statusCode: StatusCodes.Status409Conflict, title: "Flow not in catalog"));
        }

        return Parse(documents, pipeline);
    }

    /// <summary>The submission's flow: by pipeline id, by repository and name, or by a name unique among the delivery flows.</summary>
    private static async Task<(FlowContext? Flow, ProblemHttpResult? Problem)> ResolveSubmissionFlowAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, DeliverySubmissionRequest request, CancellationToken ct)
    {
        if (request.PipelineId is { } pipelineId)
        {
            return await ResolveAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request.Flow))
        {
            return (null, TypedResults.Problem(detail: "A submission names its flow: pipelineId, or flow (with repoId when the name is not unique).", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
        }

        var name = request.Flow.Trim();
        var candidates = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active && p.Kind == FlowDefinition.FlowTypeName && p.Name == name && (request.RepoId == null || p.RepoId == request.RepoId))
            .ToListAsync(ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return (null, TypedResults.Problem(detail: $"No active delivery flow '{name}'{(request.RepoId is null ? string.Empty : $" in repo '{request.RepoId}'")}.", statusCode: StatusCodes.Status404NotFound, title: "Not found"));
        }

        if (candidates.Count > 1)
        {
            return (null, TypedResults.Problem(detail: $"Flow '{name}' exists in {candidates.Count} repositories; name the repoId.", statusCode: StatusCodes.Status409Conflict, title: "Ambiguous flow"));
        }

        return Parse(documents, candidates[0]);
    }

    /// <summary>The active delivery pipeline whose flow name yields <paramref name="flowId"/>; the ledger keys flows by name.</summary>
    private static async Task<CatalogPipeline?> FindPipelineAsync(CatalogDbContext db, Guid flowId, CancellationToken ct)
    {
        var candidates = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName)
            .Select(p => new { p.Id, p.Name, p.Active })
            .ToListAsync(ct).ConfigureAwait(false);
        var match = candidates.Where(c => FlowId.Of(c.Name) == flowId).OrderByDescending(c => c.Active).FirstOrDefault();
        return match is null ? null : await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == match.Id, ct).ConfigureAwait(false);
    }

    private static Task<Guid> EnqueueRunAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, RunParameters parameters, string? pool, ClaimsPrincipal user, CancellationToken ct)
        => EnqueueRunAsync(db, dispatcher, flow, parameters, pool, user, null, ct);

    /// <summary>Queues a run of the flow, with <paramref name="companions"/> inserted in the same transaction as the run row.</summary>
    private static Task<Guid> EnqueueRunAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, RunParameters parameters, string? pool, ClaimsPrincipal user,
        IReadOnlyList<object>? companions, CancellationToken ct)
        => dispatcher.EnqueueAsync(
            db,
            new RunEnqueueRequest(
                flow.Pipeline.RepoId, flow.Pipeline.Name, flow.Pipeline.Kind, string.IsNullOrWhiteSpace(pool) ? null : pool.Trim(), null, parameters,
                RequestedBy: RequestActor.Of(user), Companions: companions),
            ct);

    /// <summary>Queues a target-side operation for a node: the flow file's location rides along, every credential
    /// stays a reference the node resolves.</summary>
    internal static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> EnqueueOperationAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, string operation, IReadOnlyDictionary<string, string> arguments,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var rootPath = await db.Repos.AsNoTracking().Where(r => r.Id == flow.Pipeline.RepoId).Select(r => r.RootPath).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return TypedResults.Problem(
                detail: "The flow's repository has no synced root path, so no node can locate the flow file for this operation.",
                statusCode: StatusCodes.Status409Conflict, title: "Repository not materialized");
        }

        var payload = new ComputeTaskPayload
        {
            Operation = operation,
            SourceRef = flow.Pipeline.Name,
            Arguments = new Dictionary<string, string>(arguments, StringComparer.Ordinal)
            {
                ["repoRoot"] = rootPath,
                ["relativePath"] = flow.Pipeline.RelativePath,
                ["actor"] = RequestActor.Label(user),
            },
        };
        payload.Validate();
        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(operation, flow.Pipeline.Name, FlowDefinition.FlowTypeName, payload.ToJson(), RequestedBy: RequestActor.Of(user)),
            ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/compute/tasks/{taskId}", new ComputeTaskAccepted(taskId, RunStatuses.Queued));
    }

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(detail: $"No {resource} '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static DeliverySubmissionDto ToDto(SubmissionState s) => new(
        s.SubmissionId, s.FlowId, s.FlowName, s.MappingReference, s.RenderContext, s.DropLocation, s.ParametersJson, s.RecordCount,
        s.Status.ToString().ToLowerInvariant(), s.ReceivedUtc, s.StartedUtc, s.CompletedUtc, s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked,
        s.Delivered, s.Held, s.Failed, s.Error, s.WorkLocation, s.BatchCount, s.Partitions, s.Reference);

    private static DeliveryWorkBatchDto ToDto(WorkBatchState b) => new(
        b.SubmissionId, b.Index, b.Location, b.RecordCount, b.Status.ToString().ToLowerInvariant(), b.LeaseOwner, b.LeaseExpiresUtc, b.RunId,
        b.CreatedUtc, b.StartedUtc, b.CompletedUtc, b.Delivered, b.Held, b.Failed, b.Retrying, b.Error);

    private static DeliveryRecordDto ToDto(RecordState r) => new(
        r.DeliveryKey.Value, r.FlowId, r.SourceKey, r.Label, r.MappingName, r.RenderContext, r.SourceFingerprint, r.SourceModifiedUtc, r.MetadataHash, r.PayloadHash, r.PayloadModifiedUtc,
        r.TargetId, r.TargetVersion, r.Status.ToString().ToLowerInvariant(), r.LastDeliveredUtc, r.LastVerifiedUtc,
        r.LastVerifyOutcome?.ToString().ToLowerInvariant(), r.LeaseOwner, r.LeaseExpiresUtc, r.LastSubmissionId, r.AttemptCount, r.NextAttemptUtc,
        r.LastError, r.PendingDocumentRef is not null, r.PendingMetadata, r.PendingPayload, r.PendingPayloadLocation, r.Blocked, r.CreatedUtc, r.UpdatedUtc,
        r.PendingDocumentRef, r.WorkBatch, ParseJsonOrNull(r.TargetStateJson), ParseJsonOrNull(r.PendingStepJson));

    private static DeliveryAttemptDto ToDto(AttemptRecord a) => new(
        a.AttemptId, a.DeliveryKey.Value, a.SubmissionId, a.RunId, a.Worker, a.StartedUtc, a.CompletedUtc, a.Outcome.ToString().ToLowerInvariant(),
        a.Phase, a.MetadataHash, a.PayloadHash, a.TargetVersion, a.Error, ParseJsonOrNull(a.ResultJson), a.WorkBatch);

    private static DeliveryActivityDto ToDto(ActivityRecord a) => new(
        a.ActivityId, a.FlowId, a.FlowName, a.Kind, a.Actor, a.StartedUtc, a.CompletedUtc, a.Outcome, a.ParametersJson, a.SubmissionId,
        a.DeliveryKey, a.RunId, a.Summary, a.Log);

    private static DeliveryMappingDto ToDto(DeliveryMapping m) => new(
        m.Id, m.RepoId, m.Reference, m.Name, m.Version, m.Kind, m.RelativePath, m.ContentHash, m.Status, m.Message, ParseJson(m.SummaryJson),
        m.FirstSeenUtc, m.LastSeenUtc);

    private static DeliveryUpdateTagDto ToDto(UpdateTag t) => new(
        t.TagId, t.Kind, t.CacheName, t.TypeName, t.ItemId, t.Path, t.Change, t.OldValue, t.NewValue, t.FromVersion, t.ToVersion, t.Mode,
        t.Status, t.Describe(), t.AffectedRecords, t.Processed, t.Remaining, t.DetectedUtc, t.DecidedUtc, t.DecidedBy,
        t.StartedUtc, t.CompletedUtc);

    private static JsonElement? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ParseJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}
