using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A finding of an inventory with how many of its ids have it, and whether a report raises it.</summary>
public sealed record DeliveryInventoryCountDto(string Finding, long Count, bool Raised);

/// <summary>An identity that created ids of an inventory, with how many of the ids a ledger claims it created.</summary>
public sealed record DeliveryInventoryOwnerDto(string Identity, long Records);

/// <summary>The owners a reconcile used: how it knew them (<c>declared</c>, <c>inferred</c> or <c>none</c>) and each identity.</summary>
public sealed record DeliveryInventoryOwnersDto(string? Source, IReadOnlyList<DeliveryInventoryOwnerDto> Identities);

/// <summary>
/// One build or reconcile of an inventory: the platform run, who asked, how it read (pages, requests), what it found and
/// changed, the ids a ledger expects it read from storage, and the counts by finding and the owners it wrote, when it reconciled.
/// </summary>
public sealed record DeliveryInventoryRunDto(
    long InventoryRunId, Guid? RunId, string Operation, string Actor, string Status, DateTime StartedUtc, DateTime? CompletedUtc, string Read,
    long Listed, int Pages, long Requests, long Added, long Changed, long Gone, long Returned, long MissingChecked,
    IReadOnlyList<DeliveryInventoryCountDto>? Findings, long? Raised, DeliveryInventoryOwnersDto? Owners, string? Error);

/// <summary>
/// An inventory as listings show it: its flow and ledger, what it reads, its last build and reconcile, the counts by finding
/// its last reconcile wrote (<c>Reconciled</c>, every finding, zeros included) with how many of them it raised, and its newest
/// run (<c>Latest</c>: one under way, or one that failed after the last reconcile).
/// </summary>
public sealed record DeliveryInventoryDto(
    int InventoryId, string Partition, Guid LedgerId, string FlowName, string Name, string Kind, string? Query, string Read, string Versions,
    DateTime CreatedUtc, DateTime UpdatedUtc, long? LastBuildRunId, DateTime? LastBuiltUtc, long? LastReconcileRunId, DateTime? LastReconciledUtc,
    IReadOnlyList<DeliveryInventoryCountDto>? Reconciled, long? Raised, DeliveryInventoryRunDto? Latest);

/// <summary>The inventories of a partition, or of every partition when none is named.</summary>
public sealed record DeliveryInventoryListDto(string? Partition, IReadOnlyList<DeliveryInventoryDto> Inventories);

/// <summary>
/// What the flow lets an operator remove of what the inventory found: the findings, whether a removal may purge, and the platform
/// the removal goes to as the flow writes it (a URL, or a reference the node resolves).
/// </summary>
public sealed record DeliveryInventoryRemovalPolicyDto(IReadOnlyList<string> Findings, bool Purge, string Endpoint);

/// <summary>
/// One inventory with its report: its counts by finding read from its rows now (every finding, zeros included), how many ids it
/// holds and how many of them are raised, the owners its last reconcile used and how it knew them, its last build and reconcile,
/// and the pipeline declaring it (with its repository, for a run of it) and what the flow says of it (<c>Declared</c> is null
/// when the flow cannot be read now), with what the flow lets an operator remove (<c>Removal</c>, left out when it allows none).
/// </summary>
public sealed record DeliveryInventoryDetailDto(
    DeliveryInventoryDto Inventory, Guid? PipelineId, Guid? RepoId, string? Description, bool? Declared, IReadOnlyList<DeliveryInventoryCountDto> Counts, long Ids,
    long Raised, DeliveryInventoryOwnersDto? Owners, DeliveryInventoryRunDto? LastBuild, DeliveryInventoryRunDto? LastReconcile, DeliveryInventoryRemovalPolicyDto? Removal);

/// <summary>
/// A removal of an inventory's ids an operator asks for: the finding, how much of each record it takes (<c>record</c>, a soft
/// delete, or <c>everything</c>, a purge), how many ids the operator was shown, the ids when they picked them, the partition it
/// acts in (<c>confirm</c>), and the pool its run is queued on.
/// </summary>
public sealed record DeliveryInventoryRemovalRequest(string? Finding, string? Scope, long? Expected, IReadOnlyList<string>? Ids, string? Confirm, string? Pool);

/// <summary>The removal queued: its run, the partition and inventory it acts in, and what it removes.</summary>
public sealed record DeliveryInventoryRemovalAccepted(Guid RunId, string Status, string Partition, string Inventory, string Finding, string Scope, long Expected);

/// <summary>
/// One removal of an inventory's ids: its run, who asked, what it removed and how much of each record, how many ids the operator
/// was shown and whether they picked them, where it stands, what it came to for the ids, why it stopped, and its audit activity.
/// </summary>
public sealed record DeliveryInventoryRemovalDto(
    long InventoryRemovalId, int InventoryId, Guid? RunId, string Actor, string Finding, string Scope, bool NamesIds, long Requested, string Status,
    DateTime StartedUtc, DateTime? CompletedUtc, long Removed, long Gone, long Skipped, long Failed, string? Error, long? ActivityId);

/// <summary>What one removal did to one id: the version it found, the finding it had, the outcome and why, and the ledger record it rested on.</summary>
public sealed record DeliveryInventoryRemovalItemDto(
    long InventoryRemovalItemId, long InventoryRemovalId, long InventoryRecordId, string TargetId, long? Version, string Finding, string Outcome, string? Reason,
    Guid? LedgerFlowId, Guid? DeliveryKey, DateTime RecordedUtc);

/// <summary>A page of what a removal did to its ids, with the id the next page starts after; left out on the last page.</summary>
public sealed record DeliveryInventoryRemovalItemPageDto(IReadOnlyList<DeliveryInventoryRemovalItemDto> Items, long? Next);

/// <summary>
/// One id of an inventory: what OSDU serves of it (kind, version, who created and last changed it and when), when the inventory
/// first listed it, last saw it change and saw it go, its finding with why (<c>Detail</c>), and what the ledgers hold of it:
/// the ledger (its identity and name), the record's delivery key, status and version, and the artifact a delivery minted with its state.
/// </summary>
public sealed record DeliveryInventoryRecordDto(
    long InventoryRecordId, int InventoryId, string TargetId, string? Kind, long? Version, string? CreateUser, DateTime? CreateTime, string? ModifyUser,
    DateTime? ModifyTime, DateTime? FirstSeenUtc, DateTime? ChangedUtc, DateTime? GoneUtc, string Finding, DateTime FindingUtc, Guid? LedgerFlowId,
    string? Ledger, Guid? DeliveryKey, string? LedgerStatus, long? LedgerVersion, long? ArtifactId, string? ArtifactState, string? Detail);

/// <summary>A page of an inventory's ids, with the id the next page starts after; left out on the last page.</summary>
public sealed record DeliveryInventoryRecordPageDto(IReadOnlyList<DeliveryInventoryRecordDto> Items, long? Next);

/// <summary>What one inventory holds of an id looked up.</summary>
public sealed record DeliveryInventoryHitDto(DeliveryInventoryDto Inventory, DeliveryInventoryRecordDto Record);

/// <summary>What every inventory of a partition holds of one OSDU id, and what removals did to it, the newest first.</summary>
public sealed record DeliveryInventoryLookupDto(
    string Partition, string TargetId, IReadOnlyList<DeliveryInventoryHitDto> Hits, IReadOnlyList<DeliveryInventoryRemovalItemDto> Removals);

/// <summary>
/// An inventory a flow declares or keeps: what the flow says of it, whether it still declares it, and the inventory as the
/// partition keeps it (left out until a build registers it).
/// </summary>
public sealed record DeliveryInventoryEntryDto(string Name, string? Description, string Kind, string? Query, string Versions, bool Declared, DeliveryInventoryDto? Inventory);

/// <summary>
/// One inventory flow in the partition it is read in: the partitions it reads and whether this one is one of them, how it
/// reads OSDU, the owners it names, the parameters a run takes, its inventories, and what keeps it from being shown (a
/// document the catalog cannot parse, a partition it does not read).
/// </summary>
public sealed record DeliveryInventoryFlowDto(
    Guid PipelineId, Guid RepoId, string Name, string? Description, string? Batch, string? Partition, bool ReadsPartition, IReadOnlyList<string> Partitions,
    Guid? LedgerId, string? Read, IReadOnlyList<string> Owners, IReadOnlyList<DeliveryParameterDto> Parameters, string? Problem,
    IReadOnlyList<DeliveryInventoryEntryDto> Inventories);

/// <summary>
/// The report of inventory flows (docs/inventory-plan.md, Stage 5): the inventories of a partition, an inventory flow's
/// inventories, one inventory with its counts by finding, owners and last runs, its ids of one finding a page at a time (keyset
/// paged by the inventory's own numbering), its runs, a lookup of an OSDU id across the partition's inventories, and an export
/// of its ids as CSV, written as it is read; and the removals an operator asks of what an inventory found (docs/inventory-plan.md,
/// Removing what an inventory found), each queued as a run of the flow and read back with what it did to every id. Every read
/// answers from the module's database, nothing here talks to OSDU, and building an inventory or removing from one is a run like
/// any other. An answer leaves out what it holds no value for.
/// </summary>
public static class DeliveryInventoryEndpoints
{
    /// <summary>The ids a page holds when the request names no limit.</summary>
    public const int DefaultPage = 100;

    /// <summary>The most ids one page holds.</summary>
    public const int MaxPage = 1000;

    /// <summary>The runs an inventory's history lists when the request names no limit.</summary>
    public const int DefaultRuns = 30;

    /// <summary>The most runs one history lists.</summary>
    public const int MaxRuns = 200;

    /// <summary>The longest OSDU id a lookup takes.</summary>
    public const int MaxIdLength = 1024;

    /// <summary>The most catalog pipelines read for the flow that declares an inventory.</summary>
    private const int MaxDeclaringPipelines = 20;

    /// <summary>How the answers are written: the API's own conventions, with what holds no value left out.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/inventories", ListAsync).WithName("ListDeliveryInventories");
        delivery.MapGet("/inventories/lookup", LookupAsync).WithName("LookupDeliveryInventoryRecords");
        delivery.MapGet("/inventories/{partition}/{inventoryId:int}", GetAsync).WithName("GetDeliveryInventory");
        delivery.MapGet("/inventories/{partition}/{inventoryId:int}/records", ListRecordsAsync).WithName("ListDeliveryInventoryRecords");
        delivery.MapGet("/inventories/{partition}/{inventoryId:int}/runs", ListRunsAsync).WithName("ListDeliveryInventoryRuns");
        delivery.MapGet("/inventories/{partition}/{inventoryId:int}/export", ExportAsync).WithName("ExportDeliveryInventoryRecords");
        delivery.MapGet("/flows/{pipelineId:guid}/inventories", GetFlowAsync).WithName("GetDeliveryInventoryFlow");
        delivery.MapGet("/inventories/{partition}/{inventoryId:int}/removals", ListRemovalsAsync).WithName("ListDeliveryInventoryRemovals");
        delivery.MapGet("/inventories/{partition}/removals/{removalId:long}", GetRemovalAsync).WithName("GetDeliveryInventoryRemoval");
        delivery.MapGet("/inventories/{partition}/removals/{removalId:long}/items", ListRemovalItemsAsync).WithName("ListDeliveryInventoryRemovalItems");
    }

    /// <summary>The removal routes, under the operate scope: a removal takes records out of OSDU.</summary>
    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/inventories/{partition}/{inventoryId:int}/removals", RemoveAsync).WithName("RemoveDeliveryInventoryRecords");
    }

    /// <summary>
    /// Queues the run that removes from OSDU the ids of one finding of an inventory, as the caller (docs/inventory-plan.md,
    /// Removing what an inventory found). Refused with 400 for a request that does not say what it removes, or whose confirmation
    /// names another partition; with 409 when the flow does not allow it, the inventory was never reconciled, it holds another
    /// number of ids of the finding than the operator was shown (or does not hold an id they picked with that finding), or a run
    /// of the flow is already queued or running. The run checks it all again on the node, and every id again before it goes.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryInventoryRemovalAccepted>, ProblemHttpResult>> RemoveAsync(
        string partition, int inventoryId, DeliveryInventoryRemovalRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        if (RemovalProblem(request, inventory) is { } refused)
        {
            return refused;
        }

        var finding = request!.Finding!.Trim().ToLowerInvariant();
        var scope = request.Scope!.Trim().ToLowerInvariant();
        var expected = request.Expected!.Value;
        var ids = request.Ids?.Select(i => i.Trim()).ToList() ?? [];
        var (pipeline, flow, declared) = await DeclaringFlowAsync(db, documents, inventory, ct).ConfigureAwait(false);
        if (pipeline is null || flow is null || declared != true)
        {
            return Problem(StatusCodes.Status409Conflict, "Not declared",
                $"No flow in the catalog declares inventory '{inventory.Name}' of '{inventory.FlowName}' now, so nothing can be removed through it.");
        }

        if (flow.Removal is not { } policy)
        {
            return Problem(StatusCodes.Status409Conflict, "Removal not allowed",
                $"Inventory flow '{flow.Name}' declares no 'removal', so it only reads OSDU. Add removal: {{ findings: [{finding}] }} to the flow to remove what it finds.");
        }

        if (!policy.Allows(finding))
        {
            return Problem(StatusCodes.Status409Conflict, "Removal not allowed",
                $"Inventory flow '{flow.Name}' allows removing {string.Join(", ", policy.Findings)} ids, not {finding} ones.");
        }

        if (scope == InventoryRemovals.Purge && !policy.Purge)
        {
            return Problem(StatusCodes.Status409Conflict, "Purge not allowed",
                $"Inventory flow '{flow.Name}' allows soft deletes only; removal.purge: true lets it purge, which destroys every version for good.");
        }

        if (inventory.LastReconcileRunId is null)
        {
            return Problem(StatusCodes.Status409Conflict, "Not reconciled",
                $"Inventory '{inventory.Name}' has not been reconciled in '{inventory.Partition}', so nothing it found can be removed yet.");
        }

        if (ids.Count == 0)
        {
            var holds = (await ledger.InventoryCountsAsync(inventory.Partition, inventory.InventoryId, ct).ConfigureAwait(false)).Of(finding);
            if (holds != expected)
            {
                return Problem(StatusCodes.Status409Conflict, "Count changed",
                    $"Inventory '{inventory.Name}' holds {holds} {finding} id(s) now, and the removal was asked for {expected}. Nothing was queued; look at it again and ask again.");
            }
        }
        else
        {
            var held = await ledger.InventoryRecordsOfAsync(inventory.Partition, inventory.InventoryId, ids, ct).ConfigureAwait(false);
            var removable = held.Where(r => r.Finding == finding && r.GoneUtc is null).Select(r => r.TargetId).ToHashSet(StringComparer.Ordinal);
            var not = ids.Where(i => !removable.Contains(i)).ToList();
            if (not.Count > 0)
            {
                return Problem(StatusCodes.Status409Conflict, "Ids changed",
                    $"{not.Count} of the ids picked {(not.Count == 1 ? "is" : "are")} not a served {finding} id of inventory '{inventory.Name}' now ({string.Join(", ", not.Take(5))}{(not.Count > 5 ? ", ..." : "")}). Nothing was queued.");
            }
        }

        var busy = await db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipeline.Id && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .Select(r => (Guid?)r.RunId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (busy is { } running)
        {
            return Problem(StatusCodes.Status409Conflict, "Flow busy",
                $"Run {running:D} of '{pipeline.Name}' is queued or running; a removal waits until the flow is idle, so what it removes is what the inventory holds. Nothing was queued.");
        }

        var removal = new InventoryRemovalRequest { Finding = finding, Scope = scope, Expected = expected, Ids = ids };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flow.Partitioned)
        {
            values[PartitionNames.RunValue] = inventory.Partition;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Remove,
            Values = values,
            Payload = new DeliveryRunPayload { Inventories = [inventory.Name], Removal = removal, Confirm = inventory.Partition }.ToJson(),
        };
        var runId = await dispatcher.EnqueueAsync(
            db,
            new RunEnqueueRequest(
                pipeline.RepoId, pipeline.Name, pipeline.Kind, string.IsNullOrWhiteSpace(request.Pool) ? null : request.Pool.Trim(), null, parameters,
                RequestedBy: RequestActor.Of(user)),
            ct).ConfigureAwait(false);
        return TypedResults.Accepted(
            $"/api/v1/runs/{runId}",
            new DeliveryInventoryRemovalAccepted(runId, RunStatuses.Queued, inventory.Partition, inventory.Name, finding, scope, expected));
    }

    /// <summary>What is wrong with a removal request on its face, before the flow and the inventory are read; null when nothing is.</summary>
    private static ProblemHttpResult? RemovalProblem(DeliveryInventoryRemovalRequest? request, InventoryState inventory)
    {
        if (request is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Removal required", "Name the finding, the scope, the count you were shown, and the partition to confirm.");
        }

        var finding = request.Finding?.Trim().ToLowerInvariant();
        if (!InventoryRemovals.IsRemovable(finding))
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown finding",
                $"'{request.Finding}' is not a finding an inventory removes; it removes {string.Join(", ", InventoryRemovals.Removable)}.");
        }

        if (!InventoryRemovals.IsScope(request.Scope?.Trim().ToLowerInvariant()))
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown scope",
                $"'{request.Scope}' is not how much a removal takes; it is {InventoryRemovals.SoftDelete} (a soft delete, reversible) or {InventoryRemovals.Purge} (a purge, every version destroyed).");
        }

        if (request.Expected is not > 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Count required", "Name how many ids you were shown (expected); nothing is removed without it.");
        }

        if (request.Ids is { } ids)
        {
            var named = ids.Select(i => i?.Trim() ?? string.Empty).ToList();
            if (named.Count == 0 || named.Count > InventoryRemovalRequest.MaxIds || named.Any(i => i.Length == 0 || i.Length > MaxIdLength))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid ids",
                    $"ids names 1 to {InventoryRemovalRequest.MaxIds} OSDU ids; leave it out to remove every id of the finding.");
            }

            if (named.Distinct(StringComparer.Ordinal).Count() != named.Count || named.Count != request.Expected)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid ids", "ids names each id once, as many as expected says.");
            }
        }

        var confirm = request.Confirm?.Trim();
        if (string.IsNullOrEmpty(confirm))
        {
            return Problem(StatusCodes.Status400BadRequest, "Confirmation required",
                "Name the partition the inventory is kept in as 'confirm'; nothing is removed without it.");
        }

        return string.Equals(confirm, inventory.Partition, StringComparison.OrdinalIgnoreCase)
            ? null
            : Problem(StatusCodes.Status400BadRequest, "Confirmation does not match",
                $"The confirmation names '{confirm}', and inventory '{inventory.Name}' is kept in partition '{inventory.Partition}'. Nothing was queued.");
    }

    /// <summary>An inventory's removals, the newest first.</summary>
    private static async Task<Results<JsonHttpResult<IReadOnlyList<DeliveryInventoryRemovalDto>>, ProblemHttpResult>> ListRemovalsAsync(
        string partition, int inventoryId, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (limit is < 1 or > MaxRuns)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is how many removals to list: 1 to {MaxRuns}.");
        }

        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        var removals = await ledger.ListInventoryRemovalsAsync(inventory.Partition, inventory.InventoryId, limit ?? DefaultRuns, ct).ConfigureAwait(false);
        return TypedResults.Json<IReadOnlyList<DeliveryInventoryRemovalDto>>(removals.Select(ToDto).ToList(), Json);
    }

    /// <summary>One removal by its partition and number.</summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryRemovalDto>, ProblemHttpResult>> GetRemovalAsync(
        string partition, long removalId, ILedger ledger, CancellationToken ct)
    {
        if (PartitionProblem(partition) is { } bad)
        {
            return bad;
        }

        var removal = await ledger.GetInventoryRemovalAsync(partition.Trim(), removalId, ct).ConfigureAwait(false);
        return removal is null
            ? Problem(StatusCodes.Status404NotFound, "Not found", $"No inventory removal {removalId} in partition '{partition.Trim()}'.")
            : TypedResults.Json(ToDto(removal), Json);
    }

    /// <summary>A page of what a removal did to its ids, of one outcome (<c>outcome</c>) or every one, after the id <c>after</c> names.</summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryRemovalItemPageDto>, ProblemHttpResult>> ListRemovalItemsAsync(
        string partition, long removalId, string? outcome, long? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (PartitionProblem(partition) is { } bad)
        {
            return bad;
        }

        var chosen = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim().ToLowerInvariant();
        if (chosen is not null && !InventoryRemovals.IsOutcome(chosen))
        {
            return Problem(StatusCodes.Status400BadRequest, "Unknown outcome", $"'{outcome}' is not what a removal comes to for an id; it is one of {string.Join(", ", InventoryRemovals.Outcomes)}.");
        }

        if (after is < 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid cursor", "after is the next of the page before: a number, zero or more.");
        }

        if (limit is < 1 or > MaxPage)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is how many ids a page holds: 1 to {MaxPage}.");
        }

        var take = limit ?? DefaultPage;
        var read = await ledger.ListInventoryRemovalItemsAsync(partition.Trim(), removalId, chosen, after, take + 1, ct).ConfigureAwait(false);
        var page = read.Count > take ? read.Take(take).ToList() : read;
        return TypedResults.Json(
            new DeliveryInventoryRemovalItemPageDto(page.Select(ToDto).ToList(), read.Count > take ? page[^1].InventoryRemovalItemId : null),
            Json);
    }

    /// <summary>The inventories of the partition the request reads (its own, else the workbench's), or of every partition.</summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryListDto>, ProblemHttpResult>> ListAsync(
        string? partition, HttpRequest request, ILedger ledger, CancellationToken ct)
    {
        var named = WorkbenchPartition.Named(partition, request);
        if (named is not null && PartitionProblem(named) is { } bad)
        {
            return bad;
        }

        var inventories = await ledger.ListInventoriesAsync(named, ct).ConfigureAwait(false);
        var listed = new List<DeliveryInventoryDto>(inventories.Count);
        foreach (var inventory in inventories)
        {
            listed.Add(await SummaryAsync(ledger, inventory, ct).ConfigureAwait(false));
        }

        return TypedResults.Json(new DeliveryInventoryListDto(named, listed), Json);
    }

    /// <summary>One inventory with its counts by finding read from its rows, its owners, its last runs and the flow declaring it.</summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryDetailDto>, ProblemHttpResult>> GetAsync(
        string partition, int inventoryId, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        var runs = await InventoryReport.RecentRunsAsync(ledger, inventory, ct).ConfigureAwait(false);
        var counts = await ledger.InventoryCountsAsync(inventory.Partition, inventory.InventoryId, ct).ConfigureAwait(false);
        var (pipeline, flow, declared) = await DeclaringFlowAsync(db, documents, inventory, ct).ConfigureAwait(false);
        var spec = declared == true ? flow?.Inventory(inventory.Name) : null;
        var owners = InventoryReport.Owners(inventory.OwnersJson, inventory.OwnersSource);
        var removal = declared == true && flow?.Removal is { } policy ? new DeliveryInventoryRemovalPolicyDto(policy.Findings, policy.Purge, flow.Source.Endpoint) : null;
        return TypedResults.Json(
            new DeliveryInventoryDetailDto(
                ToDto(inventory, runs), pipeline?.Id, pipeline?.RepoId, spec?.Description, declared, Counts(counts.ByFinding), counts.ByFinding.Values.Sum(), counts.Raised,
                owners is null ? null : ToDto(owners),
                runs.LastBuild is null ? null : ToDto(runs.LastBuild, withFindings: true),
                runs.LastReconcile is null ? null : ToDto(runs.LastReconcile, withFindings: true),
                removal),
            Json);
    }

    /// <summary>
    /// A page of an inventory's ids, of one finding (<c>finding</c>) or every one, in the order the inventory took them in, after
    /// the id <c>after</c> names: the <c>next</c> of the page before.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryRecordPageDto>, ProblemHttpResult>> ListRecordsAsync(
        string partition, int inventoryId, string? finding, long? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (FindingProblem(finding) is { } badFinding)
        {
            return badFinding;
        }

        if (after is < 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid cursor", "after is the next of the page before: a number, zero or more.");
        }

        if (limit is < 1 or > MaxPage)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is how many ids a page holds: 1 to {MaxPage}.");
        }

        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        // One more than the page is read, so the answer knows whether another page follows without a read that finds none.
        var take = limit ?? DefaultPage;
        var read = await ledger.ListInventoryRecordsAsync(inventory.Partition, inventory.InventoryId, Finding(finding), after, take + 1, ct).ConfigureAwait(false);
        var page = read.Count > take ? read.Take(take).ToList() : read;
        var names = await LedgerNamesAsync(ledger, page, ct).ConfigureAwait(false);
        return TypedResults.Json(
            new DeliveryInventoryRecordPageDto(page.Select(r => ToDto(r, names)).ToList(), read.Count > take ? page[^1].InventoryRecordId : null),
            Json);
    }

    /// <summary>An inventory's builds and reconciles, the newest first.</summary>
    private static async Task<Results<JsonHttpResult<IReadOnlyList<DeliveryInventoryRunDto>>, ProblemHttpResult>> ListRunsAsync(
        string partition, int inventoryId, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (limit is < 1 or > MaxRuns)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is how many runs to list: 1 to {MaxRuns}.");
        }

        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        var runs = await ledger.ListInventoryRunsAsync(inventory.Partition, inventory.InventoryId, limit ?? DefaultRuns, ct).ConfigureAwait(false);
        return TypedResults.Json<IReadOnlyList<DeliveryInventoryRunDto>>(runs.Select(r => ToDto(r, withFindings: true)).ToList(), Json);
    }

    /// <summary>
    /// What every inventory of the partition the request reads holds of the OSDU id <c>id</c>: each inventory listing it, or
    /// expecting it from a ledger, with the id's finding there.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryLookupDto>, ProblemHttpResult>> LookupAsync(
        string? id, string? partition, HttpRequest request, ILedger ledger, CancellationToken ct)
    {
        var named = WorkbenchPartition.Named(partition, request);
        if (named is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Partition required", "Name the partition to look the id up in (partition=), or pick one in the workbench: an id is looked up in one partition's inventories.");
        }

        if (PartitionProblem(named) is { } bad)
        {
            return bad;
        }

        var wanted = id?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Id required", "Name the OSDU id to look up (id=).");
        }

        if (wanted.Length > MaxIdLength)
        {
            return Problem(StatusCodes.Status400BadRequest, "Id too long", $"An OSDU id is at most {MaxIdLength} characters.");
        }

        var records = await ledger.LookupInventoryRecordsAsync(named, wanted, ct).ConfigureAwait(false);
        var names = await LedgerNamesAsync(ledger, records, ct).ConfigureAwait(false);
        var inventories = new Dictionary<int, InventoryState?>();
        var hits = new List<DeliveryInventoryHitDto>(records.Count);
        foreach (var record in records)
        {
            if (!inventories.TryGetValue(record.InventoryId, out var inventory))
            {
                inventory = await ledger.GetInventoryAsync(named, record.InventoryId, ct).ConfigureAwait(false);
                inventories[record.InventoryId] = inventory;
            }

            if (inventory is not null)
            {
                hits.Add(new DeliveryInventoryHitDto(ToDto(inventory, runs: null), ToDto(record, names)));
            }
        }

        var removed = await ledger.LookupInventoryRemovalItemsAsync(named, wanted, ct).ConfigureAwait(false);
        return TypedResults.Json(new DeliveryInventoryLookupDto(named, wanted, hits, removed.Select(ToDto).ToList()), Json);
    }

    /// <summary>An inventory's ids of one finding (or every one) as CSV, written a ledger page at a time as it is read.</summary>
    private static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> ExportAsync(
        string partition, int inventoryId, string? finding, ILedger ledger, CancellationToken ct)
    {
        if (FindingProblem(finding) is { } badFinding)
        {
            return badFinding;
        }

        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        // The file is written as it is read, so an inventory of millions of ids is never held whole; the request's own
        // cancellation stops it when the caller goes away.
        var chosen = Finding(finding);
        return TypedResults.Stream(
            stream => InventoryExport.WriteAsync(ledger, inventory, chosen, stream, ct),
            InventoryExport.MediaType,
            InventoryExport.FileName(inventory, chosen));
    }

    /// <summary>
    /// An inventory flow in the partition the request reads (its own, else the workbench's, else the one a run of the flow would
    /// read): every inventory it declares, each with what the partition keeps of it, and those it keeps that the flow no longer declares.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryInventoryFlowDto>, ProblemHttpResult>> GetFlowAsync(
        Guid pipelineId, string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{pipelineId}'.");
        }

        if (!string.Equals(pipeline.Kind, InventoryFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status409Conflict, "Not an inventory flow", $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not an inventory flow.");
        }

        var named = WorkbenchPartition.Named(partition, request);
        if (named is not null && PartitionProblem(named) is { } bad)
        {
            return bad;
        }

        var registered = await registry.ReadAsync(ct).ConfigureAwait(false);
        InventoryFlowDefinition flow;
        try
        {
            flow = documents.ParseInventory(pipeline.Yaml, pipeline.RelativePath);
        }
        catch (FlowValidationException ex)
        {
            return TypedResults.Json(
                new DeliveryInventoryFlowDto(
                    pipeline.Id, pipeline.RepoId, pipeline.Name, null, pipeline.Batch, named, false, [], null, null, [], [],
                    $"The catalog's copy of the flow does not parse: {ex.Message} Re-sync the repository.", []),
                Json);
        }

        var (bound, kept, why) = await SettleAsync(flow, named, ledger, registered, ct).ConfigureAwait(false);
        var parameters = flow.Parameters.Select(p => new DeliveryParameterDto(p.Key, p.Value.Required, p.Value.Default, p.Value.Description)).ToList();
        if (bound is null)
        {
            return TypedResults.Json(
                new DeliveryInventoryFlowDto(
                    pipeline.Id, pipeline.RepoId, pipeline.Name, flow.Description, flow.Batch ?? pipeline.Batch, kept, false, flow.Served(registered), null,
                    ReadName(flow.Source.Read), flow.Owners, parameters, why, Declared(flow, [])),
                Json);
        }

        var held = (await ledger.ListInventoriesAsync(kept, ct).ConfigureAwait(false)).Where(i => i.FlowId == bound.LedgerId).ToList();
        var summaries = new Dictionary<int, DeliveryInventoryDto>();
        foreach (var inventory in held)
        {
            summaries[inventory.InventoryId] = await SummaryAsync(ledger, inventory, ct).ConfigureAwait(false);
        }

        var entries = Declared(bound, held.Select(i => (State: i, Summary: summaries[i.InventoryId])).ToList());
        return TypedResults.Json(
            new DeliveryInventoryFlowDto(
                pipeline.Id, pipeline.RepoId, pipeline.Name, bound.Description, bound.Batch ?? pipeline.Batch, kept, true, bound.Served(registered), bound.LedgerId,
                ReadName(bound.Source.Read), bound.Owners, parameters, null, entries),
            Json);
    }

    /// <summary>
    /// Every inventory the flow declares, in its order, each with what the partition keeps of it; then those the partition keeps
    /// that the flow no longer declares, by name.
    /// </summary>
    private static List<DeliveryInventoryEntryDto> Declared(InventoryFlowDefinition flow, IReadOnlyList<(InventoryState State, DeliveryInventoryDto Summary)> held)
    {
        var byName = held.ToDictionary(h => h.State.Name, h => h.Summary, StringComparer.OrdinalIgnoreCase);
        var entries = flow.Inventories
            .Select(spec => new DeliveryInventoryEntryDto(
                spec.Name, spec.Description, spec.Kind, spec.Query, VersionsName(spec.Versions), true, byName.GetValueOrDefault(spec.Name)))
            .ToList();
        entries.AddRange(held
            .Where(h => flow.Inventory(h.State.Name) is null)
            .OrderBy(h => h.State.Name, StringComparer.OrdinalIgnoreCase)
            .Select(h => new DeliveryInventoryEntryDto(h.State.Name, null, h.State.Kind, h.State.Query, h.State.Versions, false, h.Summary)));
        return entries;
    }

    /// <summary>
    /// A flow bound to the partition asked for, as a run binds it, with the partition its inventories are kept in; for a flow
    /// whose partition is its header's, the flow as it is when that partition is the one its ledger is kept under (or it has
    /// not built yet, or none is asked for). Null with the reason otherwise.
    /// </summary>
    private static async Task<(InventoryFlowDefinition? Flow, string? Partition, string? Why)> SettleAsync(
        InventoryFlowDefinition flow, string? partition, ILedger ledger, RegisteredPartitions registered, CancellationToken ct)
    {
        if (!flow.Partitioned)
        {
            var kept = (await ledger.GetLedgerAsync(flow.LedgerId, ct).ConfigureAwait(false))?.Partition;
            return partition is null || kept is null || string.Equals(kept, partition, StringComparison.OrdinalIgnoreCase)
                ? (flow, kept ?? partition, null)
                : (null, kept, $"Inventory flow '{flow.Name}' reads the partition its source.headers name, '{kept}', not '{partition}'.");
        }

        try
        {
            var bound = flow.ForRun(partition, registered);
            return (bound, bound.Partition, null);
        }
        catch (DeliveryException ex)
        {
            return (null, partition, ex.Message);
        }
    }

    /// <summary>
    /// The pipeline declaring the inventory (the active one first), the flow as it declares it, and whether the flow still
    /// declares it; a null flow for a flow the catalog no longer holds or cannot read now.
    /// </summary>
    private static async Task<(CatalogPipeline? Pipeline, InventoryFlowDefinition? Flow, bool? Declared)> DeclaringFlowAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, InventoryState inventory, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == InventoryFlowDefinition.FlowTypeName && p.Name == inventory.FlowName)
            .OrderByDescending(p => p.Active)
            .Take(MaxDeclaringPipelines)
            .ToListAsync(ct).ConfigureAwait(false);
        var first = pipelines.Count > 0 ? pipelines[0] : null;
        var unreadable = false;
        foreach (var pipeline in pipelines)
        {
            try
            {
                var flow = documents.ParseInventory(pipeline.Yaml, pipeline.RelativePath);
                if (flow.Inventory(inventory.Name) is not null)
                {
                    return (pipeline, flow, true);
                }
            }
            catch (FlowValidationException)
            {
                // A copy that does not parse may yet declare it; whether it does cannot be told until it is fixed.
                unreadable = true;
            }
        }

        return (first, null, pipelines.Count == 0 || unreadable ? null : false);
    }

    /// <summary>The inventory a route names in its partition, or the problem to answer with.</summary>
    private static async Task<(InventoryState? Inventory, ProblemHttpResult? Problem)> InventoryAsync(ILedger ledger, string partition, int inventoryId, CancellationToken ct)
    {
        if (PartitionProblem(partition) is { } bad)
        {
            return (null, bad);
        }

        var inventory = await ledger.GetInventoryAsync(partition.Trim(), inventoryId, ct).ConfigureAwait(false);
        return inventory is null
            ? (null, Problem(StatusCodes.Status404NotFound, "Not found", $"No inventory {inventoryId} in partition '{partition.Trim()}'."))
            : (inventory, null);
    }

    /// <summary>An inventory as listings show it, with its newest run and the counts its last reconcile wrote.</summary>
    private static async Task<DeliveryInventoryDto> SummaryAsync(ILedger ledger, InventoryState inventory, CancellationToken ct)
        => ToDto(inventory, await InventoryReport.RecentRunsAsync(ledger, inventory, ct).ConfigureAwait(false));

    /// <summary>The names of the ledgers the records name, read once each.</summary>
    private static async Task<Dictionary<Guid, string>> LedgerNamesAsync(ILedger ledger, IReadOnlyList<InventoryRecordState> records, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var flowId in records.Select(r => r.LedgerFlowId).OfType<Guid>().Distinct())
        {
            if (await ledger.GetLedgerAsync(flowId, ct).ConfigureAwait(false) is { } entry)
            {
                names[flowId] = entry.LedgerName;
            }
        }

        return names;
    }

    private static DeliveryInventoryDto ToDto(InventoryState inventory, InventoryRecentRuns? runs)
    {
        var latest = runs?.Latest;
        var findings = InventoryReport.Findings(runs?.LastReconcile?.FindingsJson);
        return new DeliveryInventoryDto(
            inventory.InventoryId, inventory.Partition, inventory.FlowId, inventory.FlowName, inventory.Name, inventory.Kind, inventory.Query, inventory.ReadMode,
            inventory.Versions, Utc(inventory.CreatedUtc), Utc(inventory.UpdatedUtc), inventory.LastBuildRunId, Utc(inventory.LastBuiltUtc), inventory.LastReconcileRunId,
            Utc(inventory.LastReconciledUtc), findings is null ? null : Counts(findings), findings is null ? null : new InventoryCounts(findings).Raised,
            latest is null ? null : ToDto(latest, withFindings: false));
    }

    private static DeliveryInventoryRunDto ToDto(InventoryRunState run, bool withFindings)
    {
        var findings = InventoryReport.Findings(run.FindingsJson);
        var owners = withFindings ? InventoryReport.Owners(run.OwnersJson) : null;
        return new DeliveryInventoryRunDto(
            run.InventoryRunId, run.RunId, run.Operation, run.Actor, run.Status, Utc(run.StartedUtc), Utc(run.CompletedUtc), run.ReadMode, run.Listed, run.Pages,
            run.Requests, run.Added, run.Changed, run.Gone, run.Returned, run.MissingChecked,
            withFindings && findings is not null ? Counts(findings).Where(c => c.Count > 0).ToList() : null,
            findings is null ? null : new InventoryCounts(findings).Raised,
            owners is null ? null : ToDto(owners),
            run.Error);
    }

    private static DeliveryInventoryRecordDto ToDto(InventoryRecordState record, IReadOnlyDictionary<Guid, string> ledgers) => new(
        record.InventoryRecordId, record.InventoryId, record.TargetId, record.Kind, record.Version, record.CreateUser, Utc(record.CreateTime), record.ModifyUser,
        Utc(record.ModifyTime), Utc(record.FirstSeenUtc), Utc(record.ChangedUtc), Utc(record.GoneUtc), record.Finding, Utc(record.FindingUtc), record.LedgerFlowId,
        record.LedgerFlowId is { } flowId ? ledgers.GetValueOrDefault(flowId) : null, record.DeliveryKey, record.LedgerStatus, record.LedgerVersion, record.ArtifactId,
        record.ArtifactState, record.Detail);

    private static DeliveryInventoryRemovalDto ToDto(InventoryRemovalState r) => new(
        r.InventoryRemovalId, r.InventoryId, r.RunId, r.Actor, r.Finding, r.Scope, r.NamesIds, r.Requested, r.Status, Utc(r.StartedUtc), Utc(r.CompletedUtc),
        r.Removed, r.Gone, r.Skipped, r.Failed, r.Error, r.ActivityId);

    private static DeliveryInventoryRemovalItemDto ToDto(InventoryRemovalItemState i) => new(
        i.InventoryRemovalItemId, i.InventoryRemovalId, i.InventoryRecordId, i.TargetId, i.Version, i.Finding, i.Outcome, i.Reason, i.LedgerFlowId, i.DeliveryKey,
        Utc(i.RecordedUtc));

    private static DeliveryInventoryOwnersDto ToDto(InventoryOwners owners)
        => new(owners.Source, owners.Identities.Select(o => new DeliveryInventoryOwnerDto(o.Identity, o.Records)).ToList());

    private static List<DeliveryInventoryCountDto> Counts(IReadOnlyDictionary<string, long> byFinding)
        => InventoryReport.Counted(byFinding).Select(c => new DeliveryInventoryCountDto(c.Finding, c.Count, c.Raised)).ToList();

    /// <summary>The finding a request names, as the ledger keeps it; null for none.</summary>
    private static string? Finding(string? finding) => string.IsNullOrWhiteSpace(finding) ? null : finding.Trim().ToLowerInvariant();

    private static ProblemHttpResult? FindingProblem(string? finding)
        => Finding(finding) is { } named && !InventoryFindings.IsKnown(named)
            ? Problem(StatusCodes.Status400BadRequest, "Unknown finding", $"'{finding}' is not a finding; an inventory finds {string.Join(", ", InventoryFindings.All)}.")
            : null;

    private static ProblemHttpResult? PartitionProblem(string partition)
        => CacheScope.IsPartitionId(partition.Trim())
            ? null
            : Problem(StatusCodes.Status400BadRequest, "No such partition", $"'{partition}' is not a partition: a data-partition-id is letters, digits, underscore, hyphen and dot.");

    private static string ReadName(InventoryRead read) => read == InventoryRead.Storage ? "storage" : "search";

    private static string VersionsName(InventoryVersions versions) => versions == InventoryVersions.All ? "all" : "latest";

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
