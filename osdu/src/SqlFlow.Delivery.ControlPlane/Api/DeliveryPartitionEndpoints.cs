using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One OSDU partition (docs/partitions-design.md section 2.1): whether it is registered and the default, and what it holds
/// (the current version of its cache, when it was captured, how many types and records it holds, the cache flows that fill
/// it, the delivery flows that deliver to it, the changes waiting for a decision). What the workbench's partition switcher
/// and the Partitions page list, so each partition says what it is for and what it holds before it is picked.
/// </summary>
/// <param name="Name">The partition, as flows and runs name it and as its cache and ledgers are keyed.</param>
/// <param name="Description">What it is for, in the words of whoever registered it; null when unregistered or undescribed.</param>
/// <param name="IsDefault">True for the partition a run that names none runs in.</param>
/// <param name="Registered">
/// True when the registry holds it. An unregistered partition is still listed while something is kept under it: a flow that
/// hard-codes it, or a cache or ledger left from before it was removed.
/// </param>
/// <param name="CreatedUtc">When it was registered; null when unregistered.</param>
/// <param name="CreatedBy">Who registered it; null when unregistered.</param>
/// <param name="UpdatedUtc">When its registration last changed; null when unregistered.</param>
/// <param name="UpdatedBy">Who last changed it; null when unregistered.</param>
/// <param name="CurrentVersion">The version of its cache deliveries read, or null while it holds none.</param>
/// <param name="CapturedUtc">When that version was captured.</param>
/// <param name="Types">How many types that version holds.</param>
/// <param name="Items">How many records and lookup rows that version holds.</param>
/// <param name="CacheFlows">The cache flows that fill its cache, by name, in order.</param>
/// <param name="DeliveryFlows">
/// The delivery flows that deliver to it, by name, in order: those the sync describes in it, and those whose ledger it keeps,
/// a flow whose partition is its header's and a flow of a partition since removed included.
/// </param>
/// <param name="PendingChanges">Cache changes found in it that wait for someone to approve or reject them.</param>
/// <param name="Ledgers">How many ledgers the ledger's directory keeps under it: one per interface that delivered or retrieved there.</param>
public sealed record DeliveryPartitionDto(
    string Name,
    string? Description,
    bool IsDefault,
    bool Registered,
    DateTime? CreatedUtc,
    string? CreatedBy,
    DateTime? UpdatedUtc,
    string? UpdatedBy,
    string? CurrentVersion,
    DateTime? CapturedUtc,
    int Types,
    long Items,
    IReadOnlyList<string> CacheFlows,
    IReadOnlyList<string> DeliveryFlows,
    long PendingChanges,
    int Ledgers = 0);

/// <summary>A partition to register: its name, what it is for, and whether it becomes the default.</summary>
public sealed record DeliveryPartitionAddRequest(string? Name, string? Description, bool? IsDefault);

/// <summary>What a partition is for; empty clears it.</summary>
public sealed record DeliveryPartitionDescribeRequest(string? Description);

/// <summary>
/// The partition registry and the partitions the catalog knows: the listing every OSDU page is read in, and the registry's
/// upkeep. Reading is anyone's; registering, describing, changing the default and removing are admin work.
/// </summary>
/// <remarks>
/// The rows the repository sync writes per partition (a registry-driven flow's interfaces and cache types) follow the
/// registry at each sync, so every change here makes each repository source due at once: a partition just registered is
/// described, and one just removed stops being described, within one sync rather than one poll interval.
/// </remarks>
public static partial class DeliveryPartitionEndpoints
{
    /// <summary>Maps the partition listing and the registry's upkeep under the module's group.</summary>
    public static void Map(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/partitions", ListAsync).WithName("ListDeliveryPartitions");
        delivery.MapPost("/partitions", AddAsync).WithName("RegisterDeliveryPartition").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapPut("/partitions/{name}", DescribeAsync).WithName("DescribeDeliveryPartition").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapPost("/partitions/{name}/default", MakeDefaultAsync).WithName("MakeDefaultDeliveryPartition").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapDelete("/partitions/{name}", RemoveAsync).WithName("RemoveDeliveryPartition").RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>
    /// Every registered partition, and every partition something is still kept under (a cache, a synced delivery flow that
    /// hard-codes it, a ledger), ordered by name. A cache flow whose header names a partition the sync could not resolve keeps the
    /// reference as its scope; that is not a partition anyone can pick, so it is left out here and shown on the cache page
    /// as it is.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryPartitionDto>>> ListAsync(OsduDbContext osdu, CancellationToken ct)
        => TypedResults.Ok(await DescribeAllAsync(osdu, ct).ConfigureAwait(false));

    /// <summary>Registers a partition; the first one registered becomes the default.</summary>
    private static async Task<Results<Created<DeliveryPartitionDto>, ProblemHttpResult>> AddAsync(
        DeliveryPartitionAddRequest? request, OsduDbContext osdu, CatalogDbContext catalog, DeliveryPartitionRegistry registry, TimeProvider clock,
        RepoSyncSignal syncSignal, ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return TypedResults.Problem(
                detail: "A partition to register needs its name: the data-partition-id, as runs name it.",
                statusCode: StatusCodes.Status400BadRequest, title: "No name");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var actor = RequestActor.Label(user);
        try
        {
            var row = await registry.AddAsync(request.Name, request.Description, request.IsDefault == true, actor, now, ct).ConfigureAwait(false);
            await ResyncAsync(catalog, syncSignal, now, ct).ConfigureAwait(false);
            LogChanged(Logger(loggers), "registered", row.Name, actor);
            return TypedResults.Created($"partitions/{Uri.EscapeDataString(row.Name)}", await DescribeOneAsync(osdu, row.Name, ct).ConfigureAwait(false));
        }
        catch (FlowValidationException invalid)
        {
            return TypedResults.Problem(detail: invalid.Message, statusCode: StatusCodes.Status400BadRequest, title: "Not a partition");
        }
        catch (DeliveryException conflict)
        {
            return TypedResults.Problem(detail: conflict.Message, statusCode: StatusCodes.Status409Conflict, title: "Not registered");
        }
    }

    /// <summary>Sets what a registered partition is for.</summary>
    private static async Task<Results<Ok<DeliveryPartitionDto>, ProblemHttpResult>> DescribeAsync(
        string name, DeliveryPartitionDescribeRequest? request, OsduDbContext osdu, DeliveryPartitionRegistry registry, TimeProvider clock,
        ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var row = await registry.DescribeAsync(name, request?.Description, RequestActor.Label(user), clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            return TypedResults.Ok(await DescribeOneAsync(osdu, row.Name, ct).ConfigureAwait(false));
        }
        catch (FlowValidationException invalid)
        {
            return TypedResults.Problem(detail: invalid.Message, statusCode: StatusCodes.Status400BadRequest, title: "Description too long");
        }
        catch (PartitionNotRegisteredException missing)
        {
            return TypedResults.Problem(detail: missing.Message, statusCode: StatusCodes.Status404NotFound, title: "Not registered");
        }
    }

    /// <summary>Makes a registered partition the one a run that names none runs in.</summary>
    private static async Task<Results<Ok<DeliveryPartitionDto>, ProblemHttpResult>> MakeDefaultAsync(
        string name, OsduDbContext osdu, DeliveryPartitionRegistry registry, TimeProvider clock, ClaimsPrincipal user, ILoggerFactory loggers,
        CancellationToken ct)
    {
        var actor = RequestActor.Label(user);
        try
        {
            var row = await registry.MakeDefaultAsync(name, actor, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            LogChanged(Logger(loggers), "made the default", row.Name, actor);
            return TypedResults.Ok(await DescribeOneAsync(osdu, row.Name, ct).ConfigureAwait(false));
        }
        catch (PartitionNotRegisteredException missing)
        {
            return TypedResults.Problem(detail: missing.Message, statusCode: StatusCodes.Status404NotFound, title: "Not registered");
        }
        catch (DeliveryException conflict)
        {
            return TypedResults.Problem(detail: conflict.Message, statusCode: StatusCodes.Status409Conflict, title: "Default not changed");
        }
    }

    /// <summary>
    /// Takes a partition out of the registry. Nothing kept under it is deleted; a flow that follows the registry stops
    /// serving it, and the default is refused while other partitions are registered.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        string name, CatalogDbContext catalog, DeliveryPartitionRegistry registry, TimeProvider clock, RepoSyncSignal syncSignal, ClaimsPrincipal user,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var actor = RequestActor.Label(user);
        try
        {
            if (!await registry.RemoveAsync(name, ct).ConfigureAwait(false))
            {
                return TypedResults.Problem(
                    detail: $"Partition '{name.Trim()}' is not registered, so there is nothing to remove.",
                    statusCode: StatusCodes.Status404NotFound, title: "Not registered");
            }
        }
        catch (DeliveryException refused)
        {
            return TypedResults.Problem(detail: refused.Message, statusCode: StatusCodes.Status409Conflict, title: "Not removed");
        }

        await ResyncAsync(catalog, syncSignal, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        LogChanged(Logger(loggers), "removed", name.Trim(), actor);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Makes every enabled repository source due and wakes the sync loop, so the sync that describes each registry-driven
    /// flow in the partitions the registry holds now starts at once. The same request an operator makes with "sync now".
    /// </summary>
    private static async Task ResyncAsync(CatalogDbContext catalog, RepoSyncSignal syncSignal, DateTime nowUtc, CancellationToken ct)
    {
        var sources = await catalog.RepoSources.AsNoTracking().Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var source in sources)
        {
            await RepoSourceStore.TriggerNowAsync(catalog, source, nowUtc, ct).ConfigureAwait(false);
        }

        syncSignal.Wake();
    }

    private static async Task<DeliveryPartitionDto> DescribeOneAsync(OsduDbContext osdu, string name, CancellationToken ct)
        => (await DescribeAllAsync(osdu, ct).ConfigureAwait(false)).First(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>The listing <see cref="ListAsync"/> answers with.</summary>
    internal static async Task<IReadOnlyList<DeliveryPartitionDto>> DescribeAllAsync(OsduDbContext osdu, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        var registered = await osdu.DeliveryPartitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var cacheFlows = await osdu.DeliveryCacheDefinitions.AsNoTracking()
            .Select(d => new { d.Scope, d.FlowName })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var described = await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.Active && i.Partition != "")
            .Select(i => new { i.Partition, i.FlowName })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        // The ledgers each partition keeps, from the ledger's directory: one row per partition, kind and flow, counted there.
        var kept = await osdu.DeliveryLedgers.AsNoTracking()
            .Join(osdu.DeliveryLedgerPartitions.AsNoTracking(), l => l.PartitionId, p => p.PartitionId, (l, p) => new { Partition = p.Name, l.Kind, l.FlowName })
            .GroupBy(x => new { x.Partition, x.Kind, x.FlowName })
            .Select(g => new { g.Key.Partition, g.Key.Kind, g.Key.FlowName, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        var deliveryFlows = described
            .Select(d => (d.Partition, d.FlowName))
            .Concat(kept.Where(k => k.Kind == LedgerKinds.Delivery).Select(k => (k.Partition, k.FlowName)))
            .ToList();
        var current = (await osdu.DeliveryCacheVersions.AsNoTracking()
                .Where(v => v.Current)
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(OsduCacheStore.Info)
            .ToDictionary(v => v.Scope, StringComparer.OrdinalIgnoreCase);
        var pending = (await osdu.DeliveryUpdateTags.AsNoTracking()
                .Where(t => t.Status == "pending")
                .GroupBy(t => t.Scope)
                .Select(g => new { Scope = g.Key, Count = g.LongCount() })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(g => g.Scope, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.OrdinalIgnoreCase);

        // A partition is spelled as the registry spells it, and otherwise as the first thing kept under it does; the
        // comparison ignores case, as the registry's does.
        var names = registered.Select(r => r.Name)
            .Concat(cacheFlows.Select(c => c.Scope))
            .Concat(deliveryFlows.Select(d => d.Partition))
            .Concat(kept.Select(k => k.Partition))
            .Concat(current.Keys)
            .Where(CacheScope.IsPartitionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Select(name =>
        {
            var row = registered.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            var version = current.GetValueOrDefault(name);
            return new DeliveryPartitionDto(
                name,
                row?.Description,
                row?.IsDefault ?? false,
                row is not null,
                row?.CreatedUtc,
                row?.CreatedBy,
                row?.UpdatedUtc,
                row?.UpdatedBy,
                version?.Version,
                version?.CapturedUtc,
                version?.Types.Count ?? 0,
                version?.Items ?? 0,
                cacheFlows.Where(c => string.Equals(c.Scope, name, StringComparison.OrdinalIgnoreCase)).Select(c => c.FlowName)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                deliveryFlows.Where(d => string.Equals(d.Partition, name, StringComparison.OrdinalIgnoreCase)).Select(d => d.FlowName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                pending.GetValueOrDefault(name),
                kept.Where(k => string.Equals(k.Partition, name, StringComparison.OrdinalIgnoreCase)).Sum(k => k.Count));
        }).ToList();
    }

    private static ILogger Logger(ILoggerFactory loggers) => loggers.CreateLogger("SqlFlow.Delivery.Partitions");

    [LoggerMessage(Level = LogLevel.Information, Message = "Partition '{Partition}' {Change} by {Actor}.")]
    private static partial void LogChanged(ILogger logger, string change, string partition, string actor);
}
