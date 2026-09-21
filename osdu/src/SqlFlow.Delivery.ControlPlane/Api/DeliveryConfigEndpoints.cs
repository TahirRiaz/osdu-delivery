using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>One configuration property as a listing shows it. The value is shown: a property never holds a secret.</summary>
/// <param name="Name">The reference name, as a flow spells it in <c>${env:NAME}</c>.</param>
/// <param name="Value">The value, or the reference a node resolves.</param>
/// <param name="RepoId">The repository this value is set for, or null for the control plane's own.</param>
/// <param name="RepoName">That repository's name, or null for the control plane's own.</param>
/// <param name="Description">What the property is for.</param>
/// <param name="UpdatedUtc">When it was last set.</param>
/// <param name="UpdatedBy">Who last set it.</param>
public sealed record DeliveryConfigPropertyDto(
    string Name, string Value, Guid? RepoId, string? RepoName, string? Description, DateTime UpdatedUtc, string UpdatedBy);

/// <summary>What a caller sets: the value and, optionally, what it is for.</summary>
public sealed record DeliveryConfigSetRequest(string? Value, string? Description);

/// <summary>
/// The central configuration of the delivery module: the values the control plane supplies to the runs it queues, so an
/// estate names where it delivers in one place instead of in every flow document and on every node.
/// </summary>
/// <remarks>
/// <para>
/// A property is set for the whole control plane, or for one repository, which overrides the control plane's value for
/// the flows that repository holds. Setting and removing are admin work; reading is not, because what an estate delivers
/// to is what every operator reading a record needs to know.
/// </para>
/// <para>
/// A value is a non-secret value or a <c>${env:...}</c> or <c>${keyvault:...}</c> reference the node resolves, so a
/// listing can show every value without showing a secret.
/// </para>
/// </remarks>
public static class DeliveryConfigEndpoints
{
    /// <summary>Maps the configuration endpoints under the module's group.</summary>
    public static void Map(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/config", ListAsync).WithName("ListDeliveryConfig");
        delivery.MapPut("/config/{name}", SetAsync).WithName("SetDeliveryConfig").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapDelete("/config/{name}", RemoveAsync).WithName("RemoveDeliveryConfig").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapGet("/config/effective/{repoId:guid}", EffectiveAsync).WithName("GetEffectiveDeliveryConfig");
    }

    /// <summary>Every property of every scope, with the repository each belongs to named.</summary>
    private static async Task<Ok<IReadOnlyList<DeliveryConfigPropertyDto>>> ListAsync(
        CatalogDbContext db, DeliveryConfigStore config, CancellationToken ct)
    {
        var rows = await config.ListAllAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(await DescribeAsync(db, rows, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// What a run of a flow of this repository would be given: the control plane's properties with the repository's own
    /// over them, which is what the node resolves against. The answer of the question an operator actually asks.
    /// </summary>
    private static async Task<Ok<IReadOnlyDictionary<string, string>>> EffectiveAsync(
        Guid repoId, DeliveryConfigStore config, CancellationToken ct)
        => TypedResults.Ok(await config.EffectiveAsync(repoId, ct).ConfigureAwait(false));

    /// <summary>Sets a property for the control plane, or for one repository when <c>repoId</c> is given.</summary>
    private static async Task<Results<Ok<DeliveryConfigPropertyDto>, ProblemHttpResult>> SetAsync(
        string name,
        [FromQuery] Guid? repoId,
        DeliveryConfigSetRequest? request,
        CatalogDbContext db,
        DeliveryConfigStore config,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Value))
        {
            return TypedResults.Problem(
                detail: "A configuration property needs a value. Remove the property instead of setting it to nothing.",
                statusCode: StatusCodes.Status400BadRequest, title: "No value");
        }

        if (await MissingRepoAsync(db, repoId, ct).ConfigureAwait(false) is { } missing)
        {
            return missing;
        }

        try
        {
            var row = await config
                .SetAsync(repoId, name, request.Value.Trim(), request.Description, RequestActor.Label(user), clock.GetUtcNow().UtcDateTime, ct)
                .ConfigureAwait(false);
            var described = await DescribeAsync(db, [row], ct).ConfigureAwait(false);
            return TypedResults.Ok(described[0]);
        }
        catch (FlowValidationException invalid)
        {
            return TypedResults.Problem(detail: invalid.Message, statusCode: StatusCodes.Status400BadRequest, title: "Not a configuration property");
        }
    }

    /// <summary>Removes a property from the control plane, or from one repository when <c>repoId</c> is given.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveAsync(
        string name, [FromQuery] Guid? repoId, CatalogDbContext db, DeliveryConfigStore config, CancellationToken ct)
    {
        if (await MissingRepoAsync(db, repoId, ct).ConfigureAwait(false) is { } missing)
        {
            return missing;
        }

        return await config.RemoveAsync(repoId, name, ct).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    /// <summary>A problem when the repository named is not one the catalog holds, or null when it is (or none was named).</summary>
    private static async Task<ProblemHttpResult?> MissingRepoAsync(CatalogDbContext db, Guid? repoId, CancellationToken ct)
    {
        if (repoId is not { } id)
        {
            return null;
        }

        var known = await db.Repos.AsNoTracking().AnyAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return known
            ? null
            : TypedResults.Problem(
                detail: $"No repository {id:D} is registered, so a configuration property cannot be set for it.",
                statusCode: StatusCodes.Status404NotFound, title: "Repository not found");
    }

    /// <summary>The rows with their repository named, so a listing reads without a second lookup per row.</summary>
    private static async Task<IReadOnlyList<DeliveryConfigPropertyDto>> DescribeAsync(
        CatalogDbContext db, IReadOnlyList<DeliveryConfigProperty> rows, CancellationToken ct)
    {
        var repoIds = rows.Where(r => r.RepoId is not null).Select(r => r.RepoId!.Value).Distinct().ToList();
        var names = repoIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Repos.AsNoTracking().Where(r => repoIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);

        return
        [
            .. rows.Select(r => new DeliveryConfigPropertyDto(
                r.Name,
                r.Value,
                r.RepoId,
                r.RepoId is { } id ? names.GetValueOrDefault(id) : null,
                r.Description,
                r.UpdatedUtc,
                r.UpdatedBy)),
        ];
    }
}
