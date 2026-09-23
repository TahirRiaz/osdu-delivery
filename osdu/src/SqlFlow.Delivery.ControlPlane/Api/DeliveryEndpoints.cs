using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A delivery flow's record counts by state, drift, throughput, and its last submission: the flow's dashboard card. For one
/// interface it carries the interface and its ledger identity; for a source as a whole it adds its interfaces up, carries
/// no single ledger identity (<see cref="Guid.Empty"/>) and no interface.
/// </summary>
public sealed record DeliveryFlowStatsDto(
    Guid PipelineId, string FlowName, Guid FlowId, long Total, long Pending, long Delivering, long Delivered, long Held, long Failed,
    long Deleted, long Drifted, long DeliveredLast24h, DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, long Submissions,
    DeliverySubmissionDto? LastSubmission, string? Interface = null, int Interfaces = 1, long Waiting = 0);

/// <summary>
/// One interface of a delivery flow (docs/interfaces-design.md): its ledger identity, how it is delivered and why, the
/// mapping and kind it delivers, the record table it reads, what it waits for, and its record counts. A flow in the single
/// form lists one, with no name.
/// <para><c>After</c> is what the document declares; <c>Wave</c>, <c>WaitsFor</c> and <c>NotWaitedFor</c> are the order a
/// run takes, from <c>after:</c> and the relationships the mappings fill. When that order cannot be worked out (a mapping
/// or template the catalog does not hold, interfaces that wait for each other), <c>OrderProblem</c> says why and the
/// order shown is the one <c>after:</c> alone gives.</para>
/// </summary>
public sealed record DeliveryInterfaceDto(
    string? Interface, Guid FlowId, string Ledger, string Route, string? RouteReason, string Mapping, string? Kind, string RecordObject,
    IReadOnlyList<string> After, DeliveryFlowStatsDto Stats,
    int Wave = 1, IReadOnlyList<DeliveryInterfaceWaitDto>? WaitsFor = null, IReadOnlyList<DeliveryInterfaceWaitDto>? NotWaitedFor = null,
    string? OrderProblem = null);

/// <summary>One interface another waits for, or does not wait for, with where that comes from (<c>after</c> or <c>schema</c>) and why.</summary>
public sealed record DeliveryInterfaceWaitDto(string Interface, string Origin, string Why);

/// <summary>
/// One plan of a flow over its ingestion tables as the ledger received it, and what became of it: which selection it
/// read (<c>Kind</c>, the window and what else bounded it), where the records came from (<c>SourceConnection</c> as the
/// flow declares it, <c>SourceObject</c>), and the run that carried it.
/// </summary>
public sealed record DeliverySubmissionDto(
    Guid SubmissionId, Guid FlowId, string FlowName, string MappingReference, string RenderContext,
    string ParametersJson, long RecordCount, string Status, DateTime ReceivedUtc, DateTime? StartedUtc, DateTime? CompletedUtc,
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held, long Failed, string? Error,
    string? WorkLocation, int BatchCount, int Slices,
    string Kind = SubmissionKinds.Incremental, long Untracked = 0,
    string SourceConnection = "", string SourceObject = "", DateTime? WindowFromUtc = null, DateTime? WindowToUtc = null,
    JsonElement? SourceWindow = null, Guid? RunId = null, long Waiting = 0);

/// <summary>One retrieval run of a retrieval flow: the window it covered, where its files went, and its outcome.</summary>
public sealed record DeliveryRetrievalDto(
    long RetrievalId, Guid FlowId, string FlowName, Guid? RunId, string Actor, string Kinds, string? Query, string? WindowField,
    DateTime? WindowFrom, DateTime? WindowTo, string Location, string? ManifestLocation, string Status, long Records, int Files, long Bytes,
    DateTime StartedUtc, DateTime? CompletedUtc, string? Error);

/// <summary>One work batch of a submission: a file of rendered documents and how far its drain got.</summary>
public sealed record DeliveryWorkBatchDto(
    Guid SubmissionId, int Index, string Location, int RecordCount, string Status, string? LeaseOwner, DateTime? LeaseExpiresUtc, Guid? RunId,
    DateTime CreatedUtc, DateTime? StartedUtc, DateTime? CompletedUtc, long Delivered, long Held, long Failed, long Retrying, string? Error,
    long Waiting = 0);

/// <summary>The current state of one deliverable: what OSDU holds for it, what is pending, and why it is where it is.</summary>
public sealed record DeliveryRecordDto(
    Guid DeliveryKey, Guid FlowId, string SourceKey, string? Label, string MappingName, string? RenderContext,
    string? SourceFingerprint, DateTime? SourceModifiedUtc, string? MetadataHash, string? PayloadHash, DateTime? PayloadModifiedUtc, string? TargetId, long? TargetVersion, string Status,
    DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, string? LastVerifyOutcome, string? LeaseOwner, DateTime? LeaseExpiresUtc,
    Guid? LastSubmissionId, int AttemptCount, DateTime? NextAttemptUtc, string? LastError, bool HasPendingDocument,
    bool PendingMetadata, bool PendingPayload, string? PendingPayloadLocation, bool Blocked, DateTime CreatedUtc, DateTime UpdatedUtc,
    string? PendingDocumentRef, int? WorkBatch, JsonElement? TargetState, JsonElement? PendingSteps,
    string? SourceFileName, long? SourceRowNumber, DateTime? SourceUpdatedUtc,
    string? PendingSourceFileName, long? PendingSourceRowNumber, DateTime? PendingSourceUpdatedUtc,
    string? SourceKeyJson, DateTime? PlanRequestedUtc,
    string? WaitingFor = null, IReadOnlyList<DeliveryRecordReferenceDto>? References = null);

/// <summary>An OSDU id a record's pending document refers to, and the property of the record holding it.</summary>
public sealed record DeliveryRecordReferenceDto(string Id, string Property);

/// <summary>A record of the ledger another record waits for, or that waits for it: where it is and how it stands.</summary>
public sealed record DeliveryRecordLinkDto(
    Guid FlowId, Guid DeliveryKey, Guid? PipelineId, string? FlowName, string? Interface, string SourceKey, string? Label, string? TargetId, string Status);

/// <summary>
/// A flow the record lookup can be narrowed to: the ledger identity its records carry (<c>FlowId</c>, what the lookup's
/// <c>flowId</c> takes), and the pipeline and interface (null for the single form) it is named by. Only an identity that
/// holds records is offered.
/// </summary>
public sealed record DeliveryRecordFlowDto(Guid FlowId, Guid PipelineId, string FlowName, string? Interface);

/// <summary>
/// A record with the pipeline (and, for a source, the interface) it belongs to. The pending document itself lives in the
/// submission's work batches on storage, which the nodes read; its reference and batch are on the record. A waiting record
/// names the record it waits for (<c>WaitsOn</c>), and every record lists the records waiting for it (<c>WaitedOnBy</c>,
/// the first <see cref="DeliveryEndpoints.MaxWaitersShown"/>).
/// </summary>
public sealed record DeliveryRecordDetailDto(
    DeliveryRecordDto Record, Guid? PipelineId, Guid? RepoId, string? FlowName, string? Interface = null,
    DeliveryRecordLinkDto? WaitsOn = null, IReadOnlyList<DeliveryRecordLinkDto>? WaitedOnBy = null);

/// <summary>One delivery try, as the append-only history holds it: its outcome, and every step with what the target returned.</summary>
public sealed record DeliveryAttemptDto(
    long AttemptId, Guid DeliveryKey, Guid? SubmissionId, Guid? RunId, string Worker, DateTime StartedUtc, DateTime CompletedUtc,
    string Outcome, string Phase, string? MetadataHash, string? PayloadHash, long? TargetVersion, string? Error, JsonElement? Result, int? WorkBatch,
    string? SourceFileName, long? SourceRowNumber, DateTime? SourceUpdatedUtc);

/// <summary>One entry of the audit trail: who did what, when, with which inputs, and how it ended.</summary>
public sealed record DeliveryActivityDto(
    long ActivityId, Guid FlowId, string FlowName, string Kind, string Actor, DateTime StartedUtc, DateTime? CompletedUtc,
    string Outcome, string? ParametersJson, Guid? SubmissionId, Guid? DeliveryKey, Guid? RunId, string? Summary, string? Log);

/// <summary>A submission with the pipeline and interface that planned it, and the runs that carried it.</summary>
public sealed record DeliverySubmissionDetailDto(
    DeliverySubmissionDto Submission, Guid? PipelineId, IReadOnlyList<Guid> RunIds, string? Interface = null);

/// <summary>A mapping document as the sync found it in a repository.</summary>
public sealed record DeliveryMappingDto(
    Guid Id, Guid RepoId, string Reference, string Name, string Version, string Kind, string RelativePath, string ContentHash,
    string Status, string? Message, JsonElement Summary, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>A mapping document with its text.</summary>
public sealed record DeliveryMappingDetailDto(DeliveryMappingDto Mapping, string Yaml);

/// <summary>One cache flow's declaration of a type: the kind it searches, its query, and what a change does in its declaration.</summary>
/// <summary>
/// One cache flow's declaration of a type: where it takes the records from (<c>Origin</c>: osdu, table or dictionary), for an
/// OSDU type the kind and query it searches, for a table type the connection reference and table it reads and the key column,
/// and for a dictionary type the document's file and the name of its key.
/// </summary>
public sealed record DeliveryCacheTypeSourceDto(
    string Flow, string Origin, string? Kind, string? Query, string OnChange, string? Connection = null, string? SourceObject = null, string? KeyField = null,
    string? DictionaryPath = null);

/// <summary>One path the cache keeps for a type: the path, the name it is cached under, and the cache flows that declare it.</summary>
public sealed record DeliveryCacheFieldDto(string Path, string As, IReadOnlyList<string> Flows);

/// <summary>
/// A type of a partition's cache: the union of what its cache flows declare (each flow's kind and query, and every path any of
/// them keeps), what a change does (it waits for approval when any flow asks for that), and how many records the current
/// version holds.
/// </summary>
public sealed record DeliveryCacheTypeDto(
    string Name, string EntityType, IReadOnlyList<DeliveryCacheTypeSourceDto> Sources, IReadOnlyList<DeliveryCacheFieldDto> Fields, string OnChange, long Items,
    string Origin = CacheOrigins.OsduText, string? Key = null);

/// <summary>A schedule that refreshes a cache flow: its cadence, or that it fires behind other schedules.</summary>
public sealed record DeliveryCacheScheduleDto(Guid Id, string Name, string? Cron, int? IntervalSeconds, bool Chained);

/// <summary>
/// A cache flow that fills a partition's cache: the repository and file that define it (the file is where what it caches is
/// changed), its pipeline, the OSDU endpoint reference its OSDU types are searched on and the connection reference its table
/// types are read from (each null when it declares none of those), the schedules that refresh it, and the types it declares.
/// </summary>
public sealed record DeliveryCacheFlowDto(
    string Name, Guid RepoId, string RepoName, string RelativePath, Guid? PipelineId, string? Endpoint, IReadOnlyList<DeliveryCacheScheduleDto> Schedules,
    IReadOnlyList<string> Types, string? Connection = null);

/// <summary>
/// The cache of one OSDU partition: every cache flow that fills it, the types it holds as those flows together declare them,
/// its current version with the flow and run that wrote it, and how many versions it has. Every delivery flow that delivers
/// to the partition reads it.
/// </summary>
public sealed record DeliveryCacheDto(
    string Scope, IReadOnlyList<DeliveryCacheFlowDto> Flows, IReadOnlyList<DeliveryCacheTypeDto> Types, DeliveryCacheVersionDto? Current, int Versions);

/// <summary>
/// One cache change and what happens about it: the partition, the cached record and path that moved, the value before and
/// after, how many delivered manifest rows it reaches, and how far the rollout has carried it.
/// </summary>
public sealed record DeliveryUpdateTagDto(
    long TagId, string Kind, string Scope, string TypeName, string ItemId, string Path, string Change, string? OldValue, string? NewValue,
    string? FromVersion, string ToVersion, string Mode, string Status, string Summary, long AffectedRecords, long Processed,
    long Remaining, DateTime DetectedUtc, DateTime? DecidedUtc, string? DecidedBy, DateTime? StartedUtc, DateTime? CompletedUtc);

/// <summary>A decision on a set of tags: approve lets the next run carry the update, reject leaves OSDU as it is.</summary>
public sealed record DeliveryTagDecisionRequest(IReadOnlyList<long> TagIds, bool Approve);

public sealed record DeliveryTagDecisionResult(int Decided, bool Approved);

/// <summary>One cached value a record was built from, for its history page: the partition, the cached record and path, and what it held.</summary>
public sealed record DeliveryCacheUseDto(string Scope, string TypeName, string ItemId, string Path, string Kind, string Value);

/// <summary>One cached record: its OSDU id and the values captured at the declared paths, as one version of its partition's cache holds it.</summary>
public sealed record DeliveryCachedItemDto(
    long ItemId, string Scope, string Version, string TypeName, string EntityType, string RecordId, JsonElement Fields);

/// <summary>One type a cache version holds, how many records of it, and for a lookup table the name its key is kept under.</summary>
public sealed record DeliveryCacheVersionTypeDto(string Name, string EntityType, long Items, string? Key = null);

/// <summary>
/// One of the partition's system properties as a cache version holds it: a setting of the platform for the partition,
/// reported by one of its services (<c>indexer</c> or <c>search</c>), which is neither reference nor master data.
/// <c>State</c> is <c>Enabled</c>, <c>Disabled</c> or <c>Unknown</c>; <c>Detail</c> says why when it is unknown.
/// </summary>
public sealed record DeliveryCacheSystemPropertyDto(string Service, string Name, string State, string? Source, string? Detail);

/// <summary>
/// One version of a partition's cache: when it was captured, whether it is the version deliveries render against, the version
/// that was current before it, the cache flow and the run that wrote it and who asked (null run for an import from files),
/// where the content came from, what it holds, and the partition's system properties the capture found.
/// </summary>
public sealed record DeliveryCacheVersionDto(
    string Scope, string Version, int Sequence, DateTime CapturedUtc, bool Current, string? PreviousVersion, string Flow, Guid? RunId, string CapturedBy,
    string Origin, long Items, IReadOnlyList<DeliveryCacheVersionTypeDto> Types, IReadOnlyList<DeliveryCacheSystemPropertyDto> SystemProperties);

/// <summary>
/// What changed in a partition's cache between two versions: counts per type and a page of the records that differ. The
/// counts follow the type and search filters but not the change filter.
/// </summary>
public sealed record DeliveryCacheDiffDto(
    string Scope, string FromVersion, string ToVersion, long Changed, long Added, long Removed, IReadOnlyList<DeliveryCacheDiffTypeDto> Types,
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

/// <summary>A run was queued for a record-scoped operation (redeliver, verify).</summary>
public sealed record DeliveryRunAccepted(Guid RunId, string Status);

/// <summary>A compute task was queued for a target-side operation (probe, read-back, delete).</summary>
public sealed record ComputeTaskAccepted(Guid TaskId, string Status);

public sealed record DeliveryReleaseRequest(IReadOnlyList<Guid>? Keys);

public sealed record DeliveryReleaseResult(int Released);

/// <summary>
/// Redeliver: <c>scope</c> is the part to send again, <c>all</c> by default, <c>record</c>, or <c>files</c> or <c>bulk</c>
/// as the record's route sends them (<c>metadata</c> and <c>payload</c> name the same parts); <c>run</c> queues the deliver
/// run that sends it.
/// </summary>
public sealed record DeliveryRedeliverRequest(string? Scope = null, bool Run = true, string? Pool = null);

public sealed record DeliveryRedeliverResult(int Marked, Guid? RunId);

/// <summary>
/// Where a flow's records actually live: the endpoint and data partition every removal in the GUI names before it
/// runs, with the exact call each scope makes. The endpoint is reported as the flow declares it, secret references
/// and all, because that reference is what identifies the environment; no credential or header value is exposed.
/// <c>Ddms</c> says, for a flow on the ddms route, which collection of which DDMS its records go to, and
/// <c>RecordMethod</c> which method the record scope calls <c>RecordPath</c> with.
/// </summary>
public sealed record DeliveryTargetDto(
    Guid PipelineId, string FlowName, string Endpoint, string? DataPartition, string Protocol, string AuthType,
    string RecordPath, string HistoryPath, string EverythingPath, string? Interface = null, string? Ddms = null, string RecordMethod = "POST");

/// <summary>
/// The listing a removal is aimed at, the same filter the records list is built from. <c>SubmissionId</c> names the
/// records a submission last planned; <c>DeliveredBy</c> names the records a submission delivered, which stay its
/// however many submissions touch them afterwards: the set "the batch we ran" means.
/// </summary>
public sealed record DeliveryRecordFilterDto(
    string? Status, string? Search, string? Mode, Guid? SubmissionId, Guid? RunId, bool Drifted = false, Guid? DeliveredBy = null);

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

/// <summary>How far back the ledger's retention pass keeps its history: everything older than this many days that may be
/// aged out is, and nothing a delivered record has to stay reconstructible from ever is.</summary>
public sealed record DeliveryPruneRequest(int OlderThanDays);

/// <summary>
/// How a record is addressed in the API and the GUI: the ledger's flow id and the delivery key, together. A delivery key
/// alone names one record per flow that reads the row, and the flow id (not the pipeline) is what outlives a flow's
/// removal from its repository, so a record's history stays reachable.
/// </summary>
public static class DeliveryRecordRoutes
{
    /// <summary>The record's path segment: <c>{flowId}/{deliveryKey}</c>.</summary>
    public static string Path(Guid flowId, Guid deliveryKey) => $"{flowId:D}/{deliveryKey:D}";
}

/// <summary>What the retention pass aged out: delivery tries deleted (the latest of every record always kept), and
/// activities whose captured run log was cleared (the audit row itself is never deleted).</summary>
public sealed record DeliveryPruneResult(int AttemptsPruned, int ActivityLogsCleared);

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

    /// <summary>Runs one submission's page lists: the run that registered it and everything that has worked on it since.</summary>
    private const int MaxSubmissionRuns = 100;

    /// <summary>Cached type declarations the cache listing reads at once, across every partition.</summary>
    private const int MaxCacheDefinitions = 5000;

    public static RouteGroupBuilder MapDeliveryReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");

        // The central configuration the control plane supplies to the runs it queues.
        DeliveryConfigEndpoints.Map(delivery);
        delivery.MapGet("/flows/{pipelineId:guid}/stats", GetStatsAsync).WithName("GetDeliveryFlowStats");
        delivery.MapGet("/flows/{pipelineId:guid}/interfaces", ListInterfacesAsync).WithName("ListDeliveryInterfaces");
        delivery.MapGet("/flows/{pipelineId:guid}/records", ListRecordsAsync).WithName("ListDeliveryRecords");
        delivery.MapGet("/records", LookupRecordsAsync).WithName("LookupDeliveryRecords");
        delivery.MapGet("/records/flows", ListRecordFlowsAsync).WithName("ListDeliveryRecordFlows");
        delivery.MapGet("/flows/{pipelineId:guid}/target", GetTargetAsync).WithName("GetDeliveryTarget");
        delivery.MapGet("/flows/{pipelineId:guid}/submissions", ListSubmissionsAsync).WithName("ListDeliverySubmissions");
        delivery.MapGet("/flows/{pipelineId:guid}/retrievals", ListRetrievalsAsync).WithName("ListDeliveryRetrievals");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}", GetRecordAsync).WithName("GetDeliveryRecord");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/attempts", ListRecordAttemptsAsync).WithName("ListDeliveryRecordAttempts");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/chain", GetRecordChainAsync).WithName("GetDeliveryRecordChain");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/activities", ListRecordActivitiesAsync).WithName("ListDeliveryRecordActivities");
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
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/cache", ListRecordCacheUsesAsync).WithName("ListDeliveryRecordCacheUses");
        return group;
    }

    public static RouteGroupBuilder MapDeliveryWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");
        // The records ride in the body, so the route reads a larger body than the default and no larger than that.
        delivery.MapPost("/flows/{pipelineId:guid}/release", ReleaseFlowAsync).WithName("ReleaseDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/probe", ProbeAsync).WithName("ProbeDeliveryTarget");
        delivery.MapPost("/cache/tags/decide", DecideUpdateTagsAsync).WithName("DecideDeliveryUpdateTags");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/release", ReleaseRecordAsync).WithName("ReleaseDeliveryRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/redeliver", RedeliverAsync).WithName("RedeliverDeliveryRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/verify", VerifyRecordAsync).WithName("VerifyDeliveryRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/read", ReadRecordAsync).WithName("ReadDeliveryRecordBack");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/source", ReadSourceAsync).WithName("ReadDeliveryRecordSource");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/delete", DeleteRecordAsync).WithName("DeleteDeliveryRecord");
        delivery.MapPost("/flows/{pipelineId:guid}/records/remove", RemoveRecordsAsync).WithName("RemoveDeliveryRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/records/remove/preview", PreviewRemovalAsync).WithName("PreviewDeliveryRemoval");
        delivery.MapPost("/ledger/prune", PruneAsync).WithName("PruneDeliveryLedger").RequireAuthorization(ControlPlanePolicies.Admin);
        return group;
    }

    // ---- Reads ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A flow's dashboard card: one interface's counts when the request names it (or the flow has one), and the sum of
    /// every interface's otherwise, which is what a source as a whole holds.
    /// </summary>
    private static async Task<Results<Ok<DeliveryFlowStatsDto>, ProblemHttpResult>> GetStatsAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (source, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return problem!;
        }

        IReadOnlyList<FlowDefinition> flows;
        if (interfaceName is null)
        {
            flows = source.Source.Interfaces;
        }
        else if (Pick(source.Source, interfaceName, out var named) is { } unknown)
        {
            return unknown;
        }
        else
        {
            flows = [named!];
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var stats = new List<(FlowDefinition Flow, FlowStats Stats)>(flows.Count);
        foreach (var flow in flows)
        {
            stats.Add((flow, await ledger.StatsAsync(flow.Id, now, ct).ConfigureAwait(false)));
        }

        return TypedResults.Ok(StatsDto(source.Pipeline, stats));
    }

    /// <summary>A flow's interfaces in document order, each with how it is delivered, the order a run takes it in and its counts.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryInterfaceDto>>, ProblemHttpResult>> ListInterfacesAsync(
        Guid pipelineId, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (source, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return problem!;
        }

        // The kind each mapping fills is what the repository sync read; a mapping it could not read has none yet.
        var described = await DeliveryInterfaceCatalog.OfFlowAsync(osdu, source.Pipeline.RepoId, source.Pipeline.Name, ct).ConfigureAwait(false);
        var (order, orderProblem) = await OrderAsync(osdu, documents, engine, source, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var result = new List<DeliveryInterfaceDto>(source.Source.Interfaces.Count);
        foreach (var flow in source.Source.Interfaces)
        {
            var stats = await ledger.StatsAsync(flow.Id, now, ct).ConfigureAwait(false);
            var kind = described.FirstOrDefault(d => d.LedgerFlowId == flow.Id)?.Kind;
            var name = flow.Interface ?? string.Empty;
            result.Add(new DeliveryInterfaceDto(
                flow.Interface, flow.Id, flow.LedgerName, DeliveryProtocols.Name(flow.Target.Protocol), flow.RouteReason, flow.Render.Mapping,
                string.IsNullOrEmpty(kind) ? null : kind, flow.Source.Record.Object, flow.After, StatsDto(source.Pipeline, [(flow, stats)]),
                order.WaveOf(name),
                order.WaitsFor(name).Select(d => Wait(d, d.DependsOn)).ToList(),
                order.NotWaitedFor.Where(d => string.Equals(d.Interface, name, StringComparison.OrdinalIgnoreCase)).Select(d => Wait(d, d.DependsOn)).ToList(),
                orderProblem));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryInterfaceDto>>(result);
    }

    private static DeliveryInterfaceWaitDto Wait(InterfaceDependency dependency, string other)
        => new(other, dependency.Origin == DependencyOrigin.After ? "after" : "schema", dependency.Why);

    /// <summary>
    /// The order a run takes the source's interfaces in, worked out as the run's preflight works it out: from <c>after:</c>
    /// and the relationships the mappings fill, read from the repository's synced mappings and the catalog's templates. When
    /// a mapping or template is missing, or the interfaces wait for each other, the order <c>after:</c> alone gives is
    /// returned with the reason.
    /// </summary>
    private static async Task<(InterfaceOrderPlan Order, string? Problem)> OrderAsync(
        OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, SourceContext source, CancellationToken ct)
    {
        var names = source.Source.Interfaces.Select(f => f.Interface ?? string.Empty).ToList();
        var declared = InterfaceOrder.Declared(source.Source);
        if (names.Count < 2)
        {
            return (InterfaceOrder.Plan(names, declared, []), null);
        }

        var references = source.Source.Interfaces.Select(f => f.Render.Mapping).Distinct(StringComparer.Ordinal).ToList();
        var rows = await osdu.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == source.Pipeline.RepoId && references.Contains(m.Reference) && m.Status == "valid")
            .Select(m => new { m.Reference, m.Yaml, m.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);
        var schemas = new List<InterfaceSchema>(names.Count);
        foreach (var flow in source.Source.Interfaces)
        {
            var row = rows.FirstOrDefault(r => r.Reference == flow.Render.Mapping);
            if (row is null)
            {
                return (InterfaceOrder.Plan(names, declared, []), $"Mapping {flow.Render.Mapping} of interface '{flow.Interface}' is not among the repository's valid mappings, so only after: orders the interfaces.");
            }

            try
            {
                var mapping = documents.ParseMapping(row.Yaml, row.RelativePath);
                var schema = engine.Templates is { } templates ? await templates.LoadAsync(mapping.Template, ct).ConfigureAwait(false) : null;
                if (schema is null)
                {
                    return (InterfaceOrder.Plan(names, declared, []), $"Mapping {mapping.Reference} of interface '{flow.Interface}' pins template {mapping.Template}, which is not saved, so only after: orders the interfaces.");
                }

                schemas.Add(InterfaceSchemas.Describe(flow.Interface ?? string.Empty, mapping, OsduTemplate.From(schema)));
            }
            catch (FlowValidationException ex)
            {
                return (InterfaceOrder.Plan(names, declared, []), $"Mapping {flow.Render.Mapping} of interface '{flow.Interface}' could not be read ({ex.Message}), so only after: orders the interfaces.");
            }
        }

        try
        {
            return (InterfaceOrder.Plan(names, declared, schemas), null);
        }
        catch (DeliveryException ex)
        {
            return (InterfaceOrder.Plan(names, declared, []), ex.Message + " Until then a run refuses to start, and only after: orders the interfaces here.");
        }
    }

    /// <summary>The dashboard card of one interface, or of several added up.</summary>
    private static DeliveryFlowStatsDto StatsDto(CatalogPipeline pipeline, IReadOnlyList<(FlowDefinition Flow, FlowStats Stats)> stats)
    {
        var one = stats.Count == 1 ? stats[0].Flow : null;
        var last = stats.Select(s => s.Stats.LastSubmission).OfType<SubmissionState>().OrderByDescending(s => s.ReceivedUtc).FirstOrDefault();
        return new DeliveryFlowStatsDto(
            pipeline.Id, pipeline.Name, one?.Id ?? Guid.Empty,
            stats.Sum(s => s.Stats.Total), stats.Sum(s => s.Stats.Pending), stats.Sum(s => s.Stats.Delivering), stats.Sum(s => s.Stats.Delivered),
            stats.Sum(s => s.Stats.Held), stats.Sum(s => s.Stats.Failed), stats.Sum(s => s.Stats.Deleted), stats.Sum(s => s.Stats.Drifted),
            stats.Sum(s => s.Stats.DeliveredLast24h), stats.Max(s => s.Stats.LastDeliveredUtc), stats.Max(s => s.Stats.LastVerifiedUtc),
            stats.Sum(s => s.Stats.Submissions), last is null ? null : ToDto(last), one?.Interface, stats.Count, stats.Sum(s => s.Stats.Waiting));
    }

    private static async Task<Results<Ok<PagedResult<DeliveryRecordDto>>, ProblemHttpResult>> ListRecordsAsync(
        Guid pipelineId, string? search, string? mode, string? status, Guid? submissionId, Guid? runId, bool? drifted, Guid? deliveredBy, int? page, int? pageSize,
        [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, interfaceName, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(status, search, mode, submissionId, runId, drifted == true, deliveredBy));
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
            DeliveredBySubmissionId = filter.DeliveredBy,
            RunId = filter.RunId,
            Drifted = filter.Drifted,
        }, null);
    }

    /// <summary>
    /// A record by what an operator holds, across every flow: the Records page's lookup. A delivery key lands on the
    /// record of every flow reading that row; anything else is a prefix over the OSDU id, the source key, the label and
    /// the ingestion file name, narrowed to one custody state and to one flow's ledger identity (<paramref name="flowId"/>,
    /// one of <see cref="ListRecordFlowsAsync"/>) when asked. With no term it is the ledger's recency listing instead: the
    /// records the delivery system last took in or sent, newest first, which is what the page shows before anything is
    /// typed. Both are indexed reads, so they answer in milliseconds at production volume and reach no further than the
    /// candidate bound; a page past that bound is empty rather than a scan, and the listing has to be narrowed instead.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryRecordHitDto>>, ProblemHttpResult>> LookupRecordsAsync(
        string? search, string? status, Guid? flowId, int? page, int? pageSize, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var term = search?.Trim() is { Length: > 0 } typed ? typed : null;
        var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(status, null, null, null, null));
        if (query is null)
        {
            return invalid!;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var wanted = (long)p * size;
        var take = (int)Math.Min(wanted, RecordListing.LookupCandidateLimit);
        var skip = (p - 1) * size;
        var found = skip >= RecordListing.LookupCandidateLimit
            ? []
            : term is null
                ? await ledger.ListRecentAsync(take, query.Status, flowId, ct).ConfigureAwait(false)
                : await ledger.LookupAsync(term, take, query.Status, flowId, ct).ConfigureAwait(false);
        var items = found.Skip(skip).Take(size).ToList();

        // Fewer records than asked for means neither the recency index nor an identity index ran into its bound, so that count is exact.
        var total = found.Count < take
            ? new BoundedCount(found.Count, Exact: true)
            : term is null
                ? await ledger.CountRecentAsync(RecordListing.LookupCandidateLimit, query.Status, flowId, ct).ConfigureAwait(false)
                : await ledger.CountLookupAsync(term, RecordListing.LookupCandidateLimit, query.Status, flowId, ct).ConfigureAwait(false);

        var hits = await DeliveryRecordHits.DescribeAsync(db, osdu, items, ct, term).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryRecordHitDto>(hits, p, size, total.Count, TotalCapped: !total.Exact));
    }

    /// <summary>
    /// The flows the Records page can be narrowed to: every ledger identity the synced repositories name that holds at
    /// least one record, with the pipeline and interface a hit of it is named by, found the way a hit finds them, so the
    /// choice reads as the Flow column does. A source that delivers several interfaces is one choice per interface,
    /// because its records are kept per interface and never summed; an interface that has delivered nothing yet is not a
    /// choice, since narrowing to it could only show an empty page. A ledger no synced pipeline holds any more is not a
    /// choice either; its records still appear under every flow, as "no longer synced". Ordered by flow, then interface.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryRecordFlowDto>>> ListRecordFlowsAsync(
        CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var named = await osdu.DeliveryInterfaces.AsNoTracking()
            .Select(i => i.LedgerFlowId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var holding = await ledger.FlowsWithRecordsAsync(named, ct).ConfigureAwait(false);
        var ledgers = named.Where(holding.Contains).ToList();
        var found = await DeliveryPipelines.ForLedgersAsync(db, osdu, ledgers, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryRecordFlowDto>>(found
            .Select(f => new DeliveryRecordFlowDto(f.Key, f.Value.Pipeline.Id, f.Value.Pipeline.Name, NamedInterface(f.Value)))
            .OrderBy(f => f.FlowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Interface ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<Results<Ok<DeliveryTargetDto>, ProblemHttpResult>> GetTargetAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, interfaceName, ct).ConfigureAwait(false);
        return flow is null ? problem! : TypedResults.Ok(await ToTargetDtoAsync(osdu, flow, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliverySubmissionDto>>, ProblemHttpResult>> ListSubmissionsAsync(
        Guid pipelineId, int? max, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, interfaceName, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var submissions = await ledger.ListSubmissionsAsync(flow.FlowId, Math.Clamp(max ?? 100, 1, 1000), ct).ConfigureAwait(false);
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
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, record.FlowId, ct).ConfigureAwait(false);
        DeliveryRecordLinkDto? waitsOn = null;
        var holders = record is { Status: RecordStatus.Waiting, WaitingFor: { } waitingFor }
            ? await ledger.ListHoldersAsync(waitingFor, 1, ct).ConfigureAwait(false)
            : [];
        if (holders.Count > 0)
        {
            waitsOn = await LinkAsync(db, osdu, holders[0], ct).ConfigureAwait(false);
        }

        var waitedOnBy = new List<DeliveryRecordLinkDto>();
        if (record.TargetId is { } targetId)
        {
            foreach (var waiter in await ledger.ListWaitingForAsync(targetId, MaxWaitersShown, ct).ConfigureAwait(false))
            {
                waitedOnBy.Add(await LinkAsync(db, osdu, waiter, ct).ConfigureAwait(false));
            }
        }

        return TypedResults.Ok(new DeliveryRecordDetailDto(
            ToDto(record), found?.Pipeline.Id, found?.Pipeline.RepoId, found?.Pipeline.Name, NamedInterface(found), waitsOn, waitedOnBy));
    }

    /// <summary>How many of the records waiting for one record its page lists.</summary>
    public const int MaxWaitersShown = 50;

    /// <summary>A record as another record's page links to it: its flow, pipeline and interface, and how it stands.</summary>
    private static async Task<DeliveryRecordLinkDto> LinkAsync(CatalogDbContext db, OsduDbContext osdu, RecordState record, CancellationToken ct)
    {
        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, record.FlowId, ct).ConfigureAwait(false);
        return new DeliveryRecordLinkDto(
            record.FlowId, record.DeliveryKey.Value, found?.Pipeline.Id, found?.Pipeline.Name, NamedInterface(found),
            record.SourceKey, record.Label, record.TargetId, record.Status.ToString().ToLowerInvariant());
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAttemptDto>>, ProblemHttpResult>> ListRecordAttemptsAsync(
        Guid flowId, Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        var attempts = await ledger.ListAttemptsAsync(flowId, record.DeliveryKey, Math.Clamp(max ?? 100, 1, MaxAttempts), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAttemptDto>>(attempts.Select(ToDto).ToList());
    }

    /// <summary>
    /// Where the record is in the whole chain: the ingestion file its version came from, and every run that handled that
    /// file on its way through pre-ingestion and ingestion, read from the platform's own record of processed files. The
    /// delivery half of the chain is the record itself, which the page already holds.
    /// </summary>
    private static async Task<Results<Ok<DeliveryRecordChainDto>, ProblemHttpResult>> GetRecordChainAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        return record is null
            ? RecordNotFound(flowId, key)
            : TypedResults.Ok(await RecordChain.OfAsync(
                db, record, await SourceTableAsync(osdu, record.FlowId, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The ingestion table a flow reads its records from, which is what names the run that loaded a row. The sync
    /// records it per interface, so it is one read and it survives a document that stopped parsing; a ledger no synced
    /// interface names any more costs the ingestion stage and nothing else, and the runs that handled the file still
    /// answer. The declared interface wins over one left behind by an older sync.
    /// </summary>
    private static async Task<string?> SourceTableAsync(OsduDbContext osdu, Guid ledgerFlowId, CancellationToken ct)
        => await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.LedgerFlowId == ledgerFlowId && i.RecordObject != "")
            .OrderByDescending(i => i.Active)
            .ThenByDescending(i => i.LastSeenUtc)
            .Select(i => i.RecordObject)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private static async Task<Results<Ok<IReadOnlyList<DeliveryActivityDto>>, ProblemHttpResult>> ListRecordActivitiesAsync(
        Guid flowId, Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        var activities = await ledger.ListActivitiesAsync(
            new ActivityQuery { FlowId = flowId, DeliveryKey = key, Max = Math.Clamp(max ?? 100, 1, MaxAttempts) }, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryActivityDto>>(activities.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliverySubmissionDetailDto>, ProblemHttpResult>> GetSubmissionAsync(
        Guid submissionId, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, submission.FlowId, ct).ConfigureAwait(false);
        var runIds = await SubmissionRunsAsync(db, submissionId, submission.RunId, found?.Pipeline.Id, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliverySubmissionDetailDto(ToDto(submission), found?.Pipeline.Id, runIds, NamedInterface(found)));
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
        int? page, int? pageSize, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        Guid? flowId = null;
        if (pipelineId is { } pid)
        {
            var (flow, problem) = await ResolveAsync(db, documents, pid, interfaceName, ct).ConfigureAwait(false);
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

    private static async Task<Ok<IReadOnlyList<DeliveryMappingDto>>> ListMappingsAsync(Guid? repoId, string? status, OsduDbContext osdu, CancellationToken ct)
    {
        var query = osdu.DeliveryMappings.AsNoTracking().AsQueryable();
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

    private static async Task<Results<Ok<DeliveryMappingDetailDto>, ProblemHttpResult>> GetMappingAsync(Guid mappingId, OsduDbContext osdu, CancellationToken ct)
    {
        var row = await osdu.DeliveryMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mappingId, ct).ConfigureAwait(false);
        return row is null ? NotFound("mapping", mappingId) : TypedResults.Ok(new DeliveryMappingDetailDto(ToDto(row), row.Yaml));
    }

    /// <summary>
    /// Every partition's cache: the cache flows that fill it (the repository and the file of each, which is where what it
    /// caches is changed, its pipeline and the schedules that refresh it), the types it holds as those flows together declare
    /// them with what the current version holds of each, and the current version with the flow and run that wrote it. With
    /// <paramref name="repoId"/>, the caches the repository's cache flows fill. Read-only: a cache is defined in YAML and
    /// filled by runs.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryCacheDto>>> ListCachesAsync(Guid? repoId, CatalogDbContext db, OsduDbContext osdu, CancellationToken ct)
    {
        var definitions = await osdu.DeliveryCacheDefinitions.AsNoTracking()
            .OrderBy(c => c.Scope).ThenBy(c => c.Name).ThenBy(c => c.FlowName)
            .Take(MaxCacheDefinitions)
            .ToListAsync(ct).ConfigureAwait(false);
        if (repoId is { } r)
        {
            var filled = definitions.Where(d => d.RepoId == r).Select(d => d.Scope).ToHashSet(StringComparer.Ordinal);
            definitions = definitions.Where(d => filled.Contains(d.Scope)).ToList();
        }

        if (definitions.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>([]);
        }

        var scopes = definitions.Select(d => d.Scope).Distinct().ToList();
        var flowNames = definitions.Select(d => d.FlowName).Distinct().ToList();
        var repoIds = definitions.Select(d => d.RepoId).Distinct().ToList();
        var repoNames = await RepoNamesAsync(db, repoIds, ct).ConfigureAwait(false);
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => repoIds.Contains(p.RepoId) && p.Kind == CacheDefinition.FlowTypeName && flowNames.Contains(p.Name))
            .Select(p => new { p.Id, p.RepoId, p.Name })
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
        var currentRows = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => scopes.Contains(v.Scope) && v.Current)
            .ToListAsync(ct).ConfigureAwait(false);
        var versionCounts = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => scopes.Contains(v.Scope))
            .GroupBy(v => v.Scope)
            .Select(g => new { Scope = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Scope, g => g.Count, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var caches = definitions
            .GroupBy(d => d.Scope, StringComparer.Ordinal)
            .Select(group =>
            {
                var scope = group.Key;
                var current = currentRows.FirstOrDefault(v => v.Scope == scope) is { } row ? OsduCacheStore.Info(row) : null;
                var held = current?.Types.ToDictionary(t => t.Name, t => t.Items, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var keys = current?.Types.Where(t => t.Key is not null).ToDictionary(t => t.Name, t => t.Key, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                var flows = group
                    .GroupBy(d => (d.RepoId, d.FlowName))
                    .Select(declared =>
                    {
                        var first = declared.First();
                        var pipeline = pipelines.FirstOrDefault(p => p.RepoId == first.RepoId && p.Name == first.FlowName);
                        var refreshedBy = pipeline is null
                            ? []
                            : memberships
                                .Where(m => m.PipelineId == pipeline.Id)
                                .Join(schedules, m => m.ScheduleId, s => s.Id, (_, s) => s)
                                .OrderBy(s => s.Name, StringComparer.Ordinal)
                                .ToList();
                        return new DeliveryCacheFlowDto(
                            first.FlowName, first.RepoId, repoNames.GetValueOrDefault(first.RepoId, string.Empty), first.RelativePath, pipeline?.Id,
                            declared.Select(d => d.Endpoint).FirstOrDefault(e => e is not null), refreshedBy,
                            declared.Select(d => d.Name).Order(StringComparer.Ordinal).ToList(),
                            declared.Select(d => d.Connection).FirstOrDefault(c => c is not null));
                    })
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList();

                var types = group
                    .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(declared =>
                    {
                        var ordered = declared.OrderBy(d => d.FlowName, StringComparer.Ordinal).ToList();
                        var fields = new List<(string Path, string As, List<string> Flows)>();
                        foreach (var declaration in ordered)
                        {
                            foreach (var field in OsduCacheStore.ParseFields(declaration.FieldsJson, declaration.FlowName, declaration.Name))
                            {
                                var known = fields.FindIndex(f => f.As.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                                if (known < 0)
                                {
                                    fields.Add((field.Path, field.Name, [declaration.FlowName]));
                                }
                                else if (!fields[known].Flows.Contains(declaration.FlowName))
                                {
                                    fields[known].Flows.Add(declaration.FlowName);
                                }
                            }
                        }

                        var first = ordered[0];
                        var onChange = ordered.Any(d => d.OnChange.Equals("approve", StringComparison.OrdinalIgnoreCase)) ? "approve" : "auto";
                        return new DeliveryCacheTypeDto(
                            first.Name, first.EntityType,
                            ordered.Select(d => new DeliveryCacheTypeSourceDto(
                                d.FlowName, d.Origin, d.Kind, d.Query, d.OnChange, d.Connection, d.SourceObject, d.KeyField, d.DictionaryPath)).ToList(),
                            fields.Select(f => new DeliveryCacheFieldDto(f.Path, f.As, f.Flows)).ToList(),
                            onChange, held.GetValueOrDefault(first.Name), first.Origin, keys.GetValueOrDefault(first.Name) ?? first.KeyField);
                    })
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();

                return new DeliveryCacheDto(scope, flows, types, current is null ? null : ToVersionDto(current), versionCounts.GetValueOrDefault(scope));
            })
            .ToList();
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>(caches);
    }

    /// <summary>
    /// The cached records of one partition's cache, filtered by type and searched over every value they hold, so an operator
    /// can answer "is this unit cached, and under which id". The listing reads exactly one version: <paramref name="version"/>
    /// names it, and without one it is the current version, the one delivery flows render against.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryCachedItemDto>>, ProblemHttpResult>> ListCachedItemsAsync(
        string? scope, string? type, string? search, string? version, int? page, int? pageSize, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var partition = scope.Trim();
        var resolved = await CacheVersions.ResolveAsync(osdu, partition, version, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return string.IsNullOrWhiteSpace(version)
                ? TypedResults.Ok(new PagedResult<DeliveryCachedItemDto>([], p, size, 0))
                : UnknownCacheVersion(partition, version.Trim());
        }

        var query = CacheVersions.ItemsAt(osdu, partition, resolved.Sequence);
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
            items.Select(i => new DeliveryCachedItemDto(i.ItemId, partition, resolved.Version, i.TypeName, i.EntityType, i.RecordId, ParseJson(i.FieldsJson))).ToList(),
            p, size, total));
    }

    /// <summary>The versions of one partition's cache, newest first, each with the flow and run that wrote it: what the version picker offers.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheVersionDto>>, ProblemHttpResult>> ListCacheVersionsAsync(
        string? scope, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var versions = await CacheVersions.ListAsync(osdu, scope.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheVersionDto>>(versions.Select(ToVersionDto).ToList());
    }

    /// <summary>
    /// One partition cache's history, newest first: each version with the version written before it and how many records it
    /// changed, added and removed. Naming a <paramref name="type"/> narrows the counts to that type, so the versions that
    /// changed it can be told apart.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>, ProblemHttpResult>> ListCacheHistoryAsync(
        string? scope, string? type, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var history = await CacheVersions.HistoryAsync(osdu, scope.Trim(), string.IsNullOrWhiteSpace(type) ? null : type.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>(history
            .Select(h => new DeliveryCacheHistoryEntryDto(ToVersionDto(h.Version), h.Before, h.Changes.Changed, h.Changes.Added, h.Changes.Removed))
            .ToList());
    }

    private static DeliveryCacheVersionDto ToVersionDto(CacheVersionInfo version)
        => new(
            version.Scope, version.Version, version.Sequence, version.CapturedUtc, version.Current, version.PreviousVersion, version.FlowName, version.RunId,
            version.CapturedBy, version.Origin, version.Items, version.Types.Select(t => new DeliveryCacheVersionTypeDto(t.Name, t.EntityType, t.Items, t.Key)).ToList(),
            version.SystemProperties.Select(p => new DeliveryCacheSystemPropertyDto(p.Service, p.Name, p.State.ToString(), p.Source, p.Detail)).ToList());

    private static ProblemHttpResult NoCacheNamed()
        => TypedResults.Problem(
            title: "No partition", detail: "Name the partition whose cache to read with 'scope': its data-partition-id.", statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult UnknownCacheVersion(string scope, string version)
        => TypedResults.Problem(title: "Unknown version", detail: $"The cache of partition '{scope}' holds no version '{version}'.", statusCode: StatusCodes.Status404NotFound);

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
    /// What changed in one partition's cache between two versions: per type, the records whose captured values moved, the
    /// records the later version added and the ones it no longer holds, a page at a time. <paramref name="to"/> defaults to
    /// the current version.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCacheDiffDto>, ProblemHttpResult>> CompareCacheVersionsAsync(
        string? scope, string? from, string? to, string? type, string? change, string? search, int? page, int? pageSize,
        OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
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
        var query = new CacheComparisonQuery(scope.Trim(), from.Trim())
        {
            ToVersion = string.IsNullOrWhiteSpace(to) ? null : to.Trim(),
            Type = string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
            Change = kind,
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Skip = (int)Math.Min(int.MaxValue, (long)(p - 1) * size),
            Take = size,
        };
        var diff = await CacheVersions.CompareAsync(osdu, query, ct).ConfigureAwait(false);
        if (diff is null)
        {
            var named = query.ToVersion is null ? $"'{query.FromVersion}' (or has no current version)" : $"'{query.FromVersion}' or '{query.ToVersion}'";
            return TypedResults.Problem(
                title: "Unknown version", detail: $"The cache of partition '{query.Scope}' holds no version {named}.", statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(new DeliveryCacheDiffDto(
            diff.Scope, diff.FromVersion, diff.ToVersion, diff.Changed, diff.Added, diff.Removed,
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
    /// status (pending, approved, rolling, rejected, applied) and, with <c>scope</c>, narrowed to the changes found in one
    /// partition's cache.
    /// </summary>
    private static async Task<Ok<PagedResult<DeliveryUpdateTagDto>>> ListUpdateTagsAsync(
        string? status, string? scope, int? page, int? pageSize, ILedger ledger, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var tags = await ledger.ListTagsAsync(status, size, (p - 1) * size, scope, ct).ConfigureAwait(false);
        var total = await ledger.CountTagsAsync(status, scope, ct).ConfigureAwait(false);
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
        Guid flowId, Guid key, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        if (record.CacheSetId is not { } setId)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>([]);
        }

        var uses = await ledger.ListCacheSetAsync(setId, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>(uses
            .Select(u => new DeliveryCacheUseDto(u.Scope, u.TypeName, u.ItemId, u.Path, u.Kind.ToString().ToLowerInvariant(), u.ValueText))
            .ToList());
    }

    // ---- Interventions -------------------------------------------------------------------------------------------

    private static async Task<Results<Ok<DeliveryReleaseResult>, ProblemHttpResult>> ReleaseFlowAsync(
        Guid pipelineId, DeliveryReleaseRequest? request, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, DeliveryDocumentLoader documents, EngineContext engine,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, interfaceName, ct).ConfigureAwait(false);
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
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, ILedger ledger, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        using var runtime = FlowRuntime.ForTarget(engine, flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var released = await runtime.ReleaseAsync([record.DeliveryKey], ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryReleaseResult(released));
    }

    private static async Task<Results<Ok<DeliveryRedeliverResult>, Accepted<DeliveryRedeliverResult>, ProblemHttpResult>> RedeliverAsync(
        Guid flowId, Guid key, DeliveryRedeliverRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        // The part to send again, named by what it is on the record's route (record, files, bulk), or all of it.
        var part = string.IsNullOrWhiteSpace(request?.Scope) ? RedeliverScopes.All : request.Scope.Trim().ToLowerInvariant();
        RedeliverSelection scope;
        try
        {
            scope = RedeliverScopes.Of(part, flow.Flow);
        }
        catch (DeliveryException ex)
        {
            return TypedResults.Problem(detail: $"scope: {ex.Message}", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // With a run, the node marks and re-sends in one recorded run (the mark carries the run id and the scope);
        // without one, the mark is made here under the caller's name and the flow's next run sends the record. The run
        // reads the record by the key tuple the ledger stored, under the parameter values its last plan ran with, so a
        // flow whose scope is a parameter reads the row in the record's own scope rather than in the flow's defaults.
        if (request?.Run ?? true)
        {
            var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
            var parameters = new RunParameters
            {
                Operation = DeliveryOperations.Deliver,
                Values = SubmissionValues(last?.ParametersJson),
                Payload = new DeliveryRunPayload { RecordKeys = [key], Redeliver = part, Interface = flow.Flow.Interface }.ToJson(),
            };
            var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRedeliverResult(1, runId));
        }

        using var runtime = FlowRuntime.ForTarget(engine, flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var marked = await runtime.RedeliverAsync([record.DeliveryKey], scope, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryRedeliverResult(marked, null));
    }

    /// <summary>
    /// The record's rows as the ingestion tables hold them now (docs/stage4-design.md section 5.3): a compute task on a
    /// node, which opens the flow's source with the flow's own connection reference and reads the record by the key tuple
    /// the ledger stored. Nothing is planned and nothing is delivered; the task's result carries the record row with its
    /// system columns, its child datasets and the origin file and row the ingestion tables record.
    /// </summary>
    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> ReadSourceAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["deliveryKey"] = key.ToString("D") };
        if (record.LastSubmissionId is { } lastId
            && await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) is { ParametersJson.Length: > 2 } last)
        {
            // The scope predicate of the read is the one the record was planned under, so a flow whose scope is a
            // parameter reads the row in the record's own scope rather than in the flow's declared defaults.
            arguments["values"] = last.ParametersJson;
        }

        return await EnqueueOperationAsync(db, dispatcher, flow, ReadSourceRowOperation.OperationName, arguments, user, ct).ConfigureAwait(false);
    }

    /// <summary>The flow parameter values a submission was opened with; none when it recorded none or is unknown.</summary>
    private static IReadOnlyDictionary<string, string> SubmissionValues(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new DeliveryException($"The submission's recorded parameters are not valid JSON: {ex.Message}", ex);
        }
    }

    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> VerifyRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Verify,
            Payload = new DeliveryRunPayload { RecordKeys = [key], Force = true, Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> ReadRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
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
        Guid flowId, Guid key, DeliveryRemovalRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
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
        Guid pipelineId, DeliveryRemovalRequest request, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, interfaceName, ct).ConfigureAwait(false);
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
        Guid pipelineId, DeliveryRemovalRequest request, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, interfaceName, ct).ConfigureAwait(false);
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
            RemovalScopes.Wire(scope), records, Math.Max(0, records - neverDelivered), neverDelivered, capped, await ToTargetDtoAsync(osdu, flow, ct).ConfigureAwait(false)));
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
    /// which is where OSDU takes it; no other header is reported, since a header can carry a credential reference. On the
    /// ddms route the endpoints and the collection are those serving the kind the flow's mapping renders, as the
    /// repository sync read it.
    /// </summary>
    private static async Task<DeliveryTargetDto> ToTargetDtoAsync(OsduDbContext osdu, FlowContext flow, CancellationToken ct)
    {
        var target = flow.Flow.Target;
        target.Headers.TryGetValue("data-partition-id", out var partition);
        string? kind = null;
        string? ddms = null;
        if (DeliveryProtocols.ReachesDdms(target.Protocol))
        {
            var reference = flow.Flow.Render.Mapping;
            kind = await osdu.DeliveryMappings.AsNoTracking()
                .Where(m => m.RepoId == flow.Pipeline.RepoId && m.Reference == reference && m.Status == "valid")
                .Select(m => m.Kind)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            ddms = DdmsDescription(flow.Flow, kind);
        }

        var paths = RemovalEndpoints.Of(flow.Flow, string.IsNullOrEmpty(kind) ? null : kind);
        return new DeliveryTargetDto(
            flow.Pipeline.Id, flow.Pipeline.Name, target.Endpoint, partition, DeliveryProtocols.Name(target.Protocol),
            target.Auth.Type.ToString(), paths.Record, paths.History, paths.Everything, flow.Flow.Interface, ddms, paths.RecordMethod);
    }

    /// <summary>Where a ddms-route flow's records go, as a sentence: the collection and the DDMS serving the kind its mapping renders.</summary>
    private static string DdmsDescription(FlowDefinition flow, string? kind)
        => string.IsNullOrEmpty(kind)
            ? "The DDMS collection is chosen by the kind the flow's mapping renders, which the repository sync has not read yet."
            : DdmsRouting.Of(flow).Explain(kind);

    private static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> ProbeAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, CatalogDbContext db, DeliveryDocumentLoader documents, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, pipelineId, interfaceName, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        return await QueueProbeAsync(db, dispatcher, flow, RequestActor.Label(user), RequestActor.Of(user), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues one interface's target probe on a node: the one path an operator's "Probe target" and the scheduled probe
    /// (<see cref="Background.ScheduledTargetProbeService"/>) both take, so what a schedule reports is what a button
    /// reports. <paramref name="actor"/> is the label the node records the task under (<c>user:alice</c>,
    /// <c>service:schedule</c>), <paramref name="requestedBy"/> who asked, for the task row's audit.
    /// </summary>
    internal static Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> QueueProbeAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, string actor, string? requestedBy, CancellationToken ct)
        => EnqueueOperationAsync(
            db, dispatcher, flow, ProbeTargetOperation.OperationName, new Dictionary<string, string>(StringComparer.Ordinal), actor, requestedBy, ct);

    /// <summary>
    /// The ledger's retention pass: everything the <c>osdu</c> schema grows without bound and a delivered record does not
    /// have to stay reconstructible from, aged out at one cut-off. Attempts go through the ledger (the latest try of every
    /// record is always kept, so a record's last outcome stays explainable), and the captured run log of settled activities
    /// is cleared, which is the schema's only column with no ceiling at all. No row of the audit trail is deleted: who did
    /// what, when, with which parameters and to what outcome is what the traceability rule keeps.
    /// </summary>
    private static async Task<Results<Ok<DeliveryPruneResult>, ProblemHttpResult>> PruneAsync(
        DeliveryPruneRequest request, ILedger ledger, OsduDbContext osdu, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || request.OlderThanDays < 1)
        {
            return TypedResults.Problem(detail: "olderThanDays must be at least 1.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var olderThanUtc = clock.GetUtcNow().UtcDateTime.AddDays(-request.OlderThanDays);
        var pruned = await ledger.PruneAttemptsAsync(olderThanUtc, ct).ConfigureAwait(false);
        var cleared = await ClearActivityLogsAsync(osdu, olderThanUtc, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryPruneResult(pruned, cleared));
    }

    /// <summary>
    /// The most activities one statement clears the log of. Each batch is its own short statement, so clearing years of
    /// logs never holds a long lock on the table every run appends an activity to, and never takes enough row locks for
    /// SQL Server to lock the whole table instead.
    /// </summary>
    private const int ActivityLogBatch = 1_000;

    /// <summary>
    /// Clears the captured log of every activity that started before <paramref name="olderThanUtc"/> and has finished,
    /// and answers how many it cleared. The row stays: its flow, kind, actor, parameters, times, outcome and summary are
    /// the audit trail, and a record's own history is its attempts. Only the free-text log goes, which is the one thing in
    /// the schema with no ceiling per row beyond 200,000 characters and no lifecycle of its own. An activity still running
    /// is left alone, whatever its age, because its log is not written until it completes.
    /// </summary>
    private static async Task<int> ClearActivityLogsAsync(OsduDbContext osdu, DateTime olderThanUtc, CancellationToken ct)
    {
        var cleared = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await osdu.DeliveryActivities.AsNoTracking()
                .Where(a => a.StartedUtc < olderThanUtc && a.CompletedUtc != null && a.Log != null)
                .OrderBy(a => a.StartedUtc)
                .Select(a => a.ActivityId)
                .Take(ActivityLogBatch)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return cleared;
            }

            cleared += await osdu.DeliveryActivities
                .Where(a => batch.Contains(a.ActivityId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Log, (string?)null), ct)
                .ConfigureAwait(false);
            if (batch.Count < ActivityLogBatch)
            {
                return cleared;
            }
        }
    }

    // ---- Plumbing ------------------------------------------------------------------------------------------------

    /// <summary>A delivery pipeline, the source its catalog copy declares, and the interface of it a request acts on.</summary>
    internal sealed record FlowContext(CatalogPipeline Pipeline, SourceDefinition Source, FlowDefinition Flow)
    {
        public Guid FlowId => Flow.Id;
    }

    /// <summary>A delivery pipeline and the source its catalog copy declares.</summary>
    internal sealed record SourceContext(CatalogPipeline Pipeline, SourceDefinition Source);

    /// <summary>
    /// The delivery pipeline, and the interface of it the request names, or the problem to answer with. A flow in the single
    /// form, and a source of one interface, need no name; a source of several has to be told which.
    /// </summary>
    internal static async Task<(FlowContext? Flow, ProblemHttpResult? Problem)> ResolveAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, Guid pipelineId, string? interfaceName, CancellationToken ct)
    {
        var (source, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        return source is null ? (null, problem) : Select(source, interfaceName);
    }

    /// <summary>The delivery pipeline and its parsed source, or the problem to answer with.</summary>
    internal static async Task<(SourceContext? Source, ProblemHttpResult? Problem)> ResolveSourceAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, Guid pipelineId, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return (null, NotFound("pipeline", pipelineId));
        }

        return Parse(documents, pipeline);
    }

    internal static (SourceContext? Source, ProblemHttpResult? Problem) Parse(DeliveryDocumentLoader documents, CatalogPipeline pipeline)
    {
        if (!string.Equals(pipeline.Kind, FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, TypedResults.Problem(
                detail: $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a delivery flow.",
                statusCode: StatusCodes.Status409Conflict, title: "Not a delivery flow"));
        }

        try
        {
            return (new SourceContext(pipeline, documents.ParseSource(pipeline.Yaml, pipeline.RelativePath)), null);
        }
        catch (FlowValidationException ex)
        {
            return (null, TypedResults.Problem(
                detail: $"The catalog's copy of '{pipeline.Name}' does not parse: {ex.Message} Re-sync the repository.",
                statusCode: StatusCodes.Status409Conflict, title: "Flow document invalid"));
        }
    }

    /// <summary>The interface of a source a request names, as a flow context, or the problem to answer with.</summary>
    internal static (FlowContext? Flow, ProblemHttpResult? Problem) Select(SourceContext source, string? interfaceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (interfaceName is null && source.Source.Interfaces.Count > 1)
        {
            return (null, TypedResults.Problem(
                detail: $"Pipeline '{source.Pipeline.Name}' delivers {source.Source.Interfaces.Count} interfaces ({string.Join(", ", source.Source.Names)}); name the one this request is about with ?interface=.",
                statusCode: StatusCodes.Status400BadRequest, title: "Interface required"));
        }

        return Pick(source.Source, interfaceName, out var flow) is { } problem
            ? (null, problem)
            : (new FlowContext(source.Pipeline, source.Source, flow!), null);
    }

    /// <summary>The interface <paramref name="interfaceName"/> names (the only one when it names none), or the problem to answer with.</summary>
    private static ProblemHttpResult? Pick(SourceDefinition source, string? interfaceName, out FlowDefinition? flow)
    {
        try
        {
            flow = source.Interface(interfaceName);
            return null;
        }
        catch (DeliveryException ex)
        {
            flow = null;
            return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound, title: "No such interface");
        }
    }

    /// <summary>The interface a ledger identity's pipeline names, or null for a flow in the single form.</summary>
    private static string? NamedInterface(LedgerPipeline? found) => found is { Interface.Length: > 0 } ? found.Interface : null;

    /// <summary>
    /// One flow's record and the pipeline behind it: the delivery flow whose name yields the flow id. An intervention acts
    /// on that flow's record only, never on another flow's record of the same source row.
    /// </summary>
    private static async Task<(FlowContext? Flow, RecordState? Record, ProblemHttpResult? Problem)> ResolveForRecordAsync(
        CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, Guid flowId, Guid key, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return (null, null, RecordNotFound(flowId, key));
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, record.FlowId, ct).ConfigureAwait(false);
        if (found is null)
        {
            return (null, null, TypedResults.Problem(
                detail: "The record's flow is no longer in any synced repository, so nothing can act on it. Restore the flow document and sync.",
                statusCode: StatusCodes.Status409Conflict, title: "Flow not in catalog"));
        }

        var (source, problem) = Parse(documents, found.Pipeline);
        if (source is null)
        {
            return (null, null, problem);
        }

        var (flow, missing) = Select(source, found.Interface.Length == 0 ? null : found.Interface);
        return flow is null ? (null, null, missing) : (flow, record, null);
    }

    /// <summary>
    /// The runs that worked on one submission, newest first: the run that registered it, and every run whose kind
    /// arguments name it (a re-run, a drain, a fan-out member). A run carries the submission in its payload rather than in
    /// a column of its own, so the payload is what finds them. That match is scoped to the submission's own flow, which is
    /// the only flow those runs belong to; without the scope the search reads every run the estate has ever recorded to
    /// answer one submission's page.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SubmissionRunsAsync(
        CatalogDbContext db, Guid submissionId, Guid? runId, Guid? pipelineId, CancellationToken ct)
    {
        var named = submissionId.ToString("D");
        return await db.Runs.AsNoTracking()
            .Where(r => (runId != null && r.RunId == runId)
                || (pipelineId != null && r.PipelineId == pipelineId && r.Payload != null && r.Payload.Contains(named)))
            .OrderByDescending(r => r.EnqueuedUtc)
            .Select(r => r.RunId)
            .Take(MaxSubmissionRuns)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static Task<Guid> EnqueueRunAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, RunParameters parameters, string? pool, ClaimsPrincipal user, CancellationToken ct)
        => dispatcher.EnqueueAsync(
            db,
            new RunEnqueueRequest(
                flow.Pipeline.RepoId, flow.Pipeline.Name, flow.Pipeline.Kind, string.IsNullOrWhiteSpace(pool) ? null : pool.Trim(), null, parameters,
                RequestedBy: RequestActor.Of(user)),
            ct);

    /// <summary>A failure as it may be stored and shown: the message with every resolved secret redacted out of it.</summary>
    private static string Redacted(Exception ex) => SecretHygiene.RedactedMessage(ex);

    /// <summary>Queues a target-side operation for a node: the flow file's location rides along, every credential
    /// stays a reference the node resolves.</summary>
    internal static Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> EnqueueOperationAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, string operation, IReadOnlyDictionary<string, string> arguments,
        ClaimsPrincipal user, CancellationToken ct)
        => EnqueueOperationAsync(db, dispatcher, flow, operation, arguments, RequestActor.Label(user), RequestActor.Of(user), ct);

    /// <summary>
    /// The same, for a caller that is not a request: the actor label and the requester are given rather than read from a
    /// principal, so the scheduled work of the module queues a node operation exactly as an endpoint does.
    /// </summary>
    internal static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> EnqueueOperationAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, string operation, IReadOnlyDictionary<string, string> arguments,
        string actor, string? requestedBy, CancellationToken ct)
    {
        var rootPath = await db.Repos.AsNoTracking().Where(r => r.Id == flow.Pipeline.RepoId).Select(r => r.RootPath).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return TypedResults.Problem(
                detail: "The flow's repository has no synced root path, so no node can locate the flow file for this operation.",
                statusCode: StatusCodes.Status409Conflict, title: "Repository not materialized");
        }

        var taskArguments = new Dictionary<string, string>(arguments, StringComparer.Ordinal)
        {
            ["repoRoot"] = rootPath,
            ["relativePath"] = flow.Pipeline.RelativePath,
            ["actor"] = actor,
        };
        if (flow.Flow.Interface is { } interfaceName)
        {
            // The node acts through this interface of the source, and through no other.
            taskArguments["interface"] = interfaceName;
        }

        var payload = new ComputeTaskPayload
        {
            Operation = operation,
            SourceRef = flow.Pipeline.Name,
            Arguments = taskArguments,
        };

        // The operation is one of the module's own (the node registers it beside the platform's), so the payload is
        // checked as a registered operation's: the platform's own list of operations does not name it.
        payload.Validate([operation]);
        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(operation, flow.Pipeline.Name, FlowDefinition.FlowTypeName, payload.ToJson(), RequestedBy: requestedBy),
            ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/compute/tasks/{taskId}", new ComputeTaskAccepted(taskId, RunStatuses.Queued));
    }

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(detail: $"No {resource} '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult RecordNotFound(Guid flowId, Guid key)
        => TypedResults.Problem(detail: $"No record '{key}' in the ledger of flow '{flowId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static DeliverySubmissionDto ToDto(SubmissionState s) => new(
        s.SubmissionId, s.FlowId, s.FlowName, s.MappingReference, s.RenderContext, s.ParametersJson, s.RecordCount,
        s.Status.ToString().ToLowerInvariant(), s.ReceivedUtc, s.StartedUtc, s.CompletedUtc, s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked,
        s.Delivered, s.Held, s.Failed, s.Error, s.WorkLocation, s.BatchCount, s.Slices,
        s.Kind, s.Untracked, s.SourceConnection, s.SourceObject, s.WindowFromUtc, s.WindowToUtc,
        ParseJsonOrNull(s.SourceWindowJson), s.RunId, s.Waiting);

    private static DeliveryWorkBatchDto ToDto(WorkBatchState b) => new(
        b.SubmissionId, b.Index, b.Location, b.RecordCount, b.Status.ToString().ToLowerInvariant(), b.LeaseOwner, b.LeaseExpiresUtc, b.RunId,
        b.CreatedUtc, b.StartedUtc, b.CompletedUtc, b.Delivered, b.Held, b.Failed, b.Retrying, b.Error, b.Waiting);

    private static DeliveryRecordDto ToDto(RecordState r) => new(
        r.DeliveryKey.Value, r.FlowId, r.SourceKey, r.Label, r.MappingName, r.RenderContext, r.SourceFingerprint, r.SourceModifiedUtc, r.MetadataHash, r.PayloadHash, r.PayloadModifiedUtc,
        r.TargetId, r.TargetVersion, r.Status.ToString().ToLowerInvariant(), r.LastDeliveredUtc, r.LastVerifiedUtc,
        r.LastVerifyOutcome?.ToString().ToLowerInvariant(), r.LeaseOwner, r.LeaseExpiresUtc, r.LastSubmissionId, r.AttemptCount, r.NextAttemptUtc,
        r.LastError, r.PendingDocumentRef is not null, r.PendingMetadata, r.PendingPayload, r.PendingPayloadLocation, r.Blocked, r.CreatedUtc, r.UpdatedUtc,
        r.PendingDocumentRef, r.WorkBatch, ParseJsonOrNull(r.TargetStateJson), ParseJsonOrNull(r.PendingStepJson),
        // Where the record came from. Traceability is the product: a delivered record says which ingestion file and row
        // it was built from, and a record with work waiting says which file and row that work will be built from.
        r.SourceFileName, r.SourceRowNumber, r.SourceUpdatedUtc,
        r.PendingSourceFileName, r.PendingSourceRowNumber, r.PendingSourceUpdatedUtc,
        r.SourceKeyJson, r.PlanRequestedUtc,
        // What the pending document refers to, and, while the record waits, the record it waits for.
        r.WaitingFor, r.PendingReferences.Select(p => new DeliveryRecordReferenceDto(p.Id, p.Property)).ToList());

    private static DeliveryAttemptDto ToDto(AttemptRecord a) => new(
        a.AttemptId, a.DeliveryKey.Value, a.SubmissionId, a.RunId, a.Worker, a.StartedUtc, a.CompletedUtc, a.Outcome.ToString().ToLowerInvariant(),
        a.Phase, a.MetadataHash, a.PayloadHash, a.TargetVersion, a.Error, ParseJsonOrNull(a.ResultJson), a.WorkBatch,
        // The origin of the document this try sent, which is what makes a past attempt reconstructible from the ledger
        // alone even after the record has moved on to a newer row.
        a.SourceFileName, a.SourceRowNumber, a.SourceUpdatedUtc);

    private static DeliveryActivityDto ToDto(ActivityRecord a) => new(
        a.ActivityId, a.FlowId, a.FlowName, a.Kind, a.Actor, a.StartedUtc, a.CompletedUtc, a.Outcome, a.ParametersJson, a.SubmissionId,
        a.DeliveryKey, a.RunId, a.Summary, a.Log);

    private static DeliveryMappingDto ToDto(DeliveryMapping m) => new(
        m.Id, m.RepoId, m.Reference, m.Name, m.Version, m.Kind, m.RelativePath, m.ContentHash, m.Status, m.Message, ParseJson(m.SummaryJson),
        m.FirstSeenUtc, m.LastSeenUtc);

    private static DeliveryUpdateTagDto ToDto(UpdateTag t) => new(
        t.TagId, t.Kind, t.Scope, t.TypeName, t.ItemId, t.Path, t.Change, t.OldValue, t.NewValue, t.FromVersion, t.ToVersion, t.Mode,
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
