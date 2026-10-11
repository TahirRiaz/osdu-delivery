using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// An operator's purge of a partition cache's history: <c>Scope</c> names the partition, and <c>KeepDays</c> how many days
/// of replaced versions keep their records, from 0 (only the versions that are always kept) to 36500; left out, the
/// partition's own retention.
/// </summary>
public sealed record DeliveryCachePruneRequest(string? Scope, int? KeepDays);

/// <summary>
/// What a purge of a partition cache's history did, or for a preview would do: the days it kept, how many versions keep
/// their records, the versions it prunes (oldest first) and the stored rows that go, what it waits for when it cannot prune
/// yet, and one line saying it all.
/// </summary>
public sealed record DeliveryCachePruneResultDto(
    string Scope, int KeepDays, bool Preview, int Kept, IReadOnlyList<string> Pruned, long Rows, string? Deferred, string Summary);

/// <summary>
/// The Cache page's purge of a partition cache's history (osdu/docs/reference/concepts/partition-cache.md, Retention). It is
/// the retention every refresh applies, with the days an operator names in place of the partition's: the current version,
/// the one it replaced and every version a delivery flow pins keep their records whatever the days, a pruned version stays
/// listed with what it changed, and it records who pruned it and when. A preview says what it would prune, and changes
/// nothing; the purge itself is an admin's, since what it removes cannot be brought back.
/// </summary>
public static class DeliveryCachePruneEndpoints
{
    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/cache/prune/preview", PreviewAsync).WithName("PreviewDeliveryCachePrune");
        delivery.MapPost("/cache/prune", PruneAsync).WithName("PruneDeliveryCacheHistory").RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>What a purge keeping <c>KeepDays</c> would prune of the partition's cache; nothing changes.</summary>
    private static Task<Results<Ok<DeliveryCachePruneResultDto>, ProblemHttpResult>> PreviewAsync(
        DeliveryCachePruneRequest request, ICacheStore caches, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
        => ApplyAsync(request, caches, clock, user, dryRun: true, logger: null, ct);

    /// <summary>Prunes the partition cache's history, keeping <c>KeepDays</c>, as the requesting admin.</summary>
    private static Task<Results<Ok<DeliveryCachePruneResultDto>, ProblemHttpResult>> PruneAsync(
        DeliveryCachePruneRequest request, ICacheStore caches, TimeProvider clock, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
        => ApplyAsync(request, caches, clock, user, dryRun: false, loggers.CreateLogger(typeof(DeliveryCachePruneEndpoints).FullName!), ct);

    private static async Task<Results<Ok<DeliveryCachePruneResultDto>, ProblemHttpResult>> ApplyAsync(
        DeliveryCachePruneRequest request, ICacheStore caches, TimeProvider clock, ClaimsPrincipal user, bool dryRun, ILogger? logger, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Scope))
        {
            return Problem(StatusCodes.Status400BadRequest, "No partition", "Name the partition whose cache history to prune with 'scope': its data-partition-id.");
        }

        var scope = request.Scope.Trim();
        if (request.KeepDays is < 0 or > CacheRetention.MaxDays)
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Days out of range",
                string.Create(CultureInfo.InvariantCulture, $"'keepDays' is {request.KeepDays}; a purge keeps from 0 (only the versions that are always kept) to {CacheRetention.MaxDays} days."));
        }

        if (await caches.CurrentVersionAsync(scope, ct).ConfigureAwait(false) is null)
        {
            return Problem(StatusCodes.Status404NotFound, "No cache", $"The cache of partition '{scope}' holds no version, so it has no history to prune.");
        }

        var keepDays = request.KeepDays ?? (await caches.DeclarationAsync(scope, ct).ConfigureAwait(false)).RetentionDays;
        var actor = RequestActor.Label(user);
        var outcome = await caches.ApplyRetentionAsync(
            CacheRetentionRequest.Purge(scope, keepDays, clock.GetUtcNow(), actor, dryRun), ct).ConfigureAwait(false);
        var summary = outcome.DescribePurge();
        if (logger is not null)
        {
            logger.LogInformation(
                "{Actor} purged the history of the cache of partition {Scope}, keeping {Days} day(s): {Summary}", actor, scope, keepDays, summary);
        }

        return TypedResults.Ok(new DeliveryCachePruneResultDto(
            scope, keepDays, dryRun, outcome.Kept, outcome.Pruned, outcome.RowsRemoved, outcome.Deferred, summary));
    }

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(title: title, detail: detail, statusCode: status);
}
