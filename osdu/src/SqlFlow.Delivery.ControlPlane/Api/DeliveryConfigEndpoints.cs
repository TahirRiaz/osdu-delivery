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
/// <param name="Partition">The OSDU partition this value is set for, or null for a value that applies whatever the partition.</param>
/// <param name="Description">What the property is for.</param>
/// <param name="UpdatedUtc">When it was last set.</param>
/// <param name="UpdatedBy">Who last set it.</param>
public sealed record DeliveryConfigPropertyDto(
    string Name, string Value, Guid? RepoId, string? RepoName, string? Partition, string? Description, DateTime UpdatedUtc, string UpdatedBy);

/// <summary>What a caller sets: the value and, optionally, what it is for.</summary>
public sealed record DeliveryConfigSetRequest(string? Value, string? Description);

/// <summary>
/// The central configuration of the delivery module: the values the control plane supplies to the runs it queues, so an
/// estate names where it delivers in one place instead of in every flow document and on every node.
/// </summary>
/// <remarks>
/// <para>
/// A property is set for the whole control plane, or for one repository, which overrides the control plane's value for
/// the flows that repository holds. Either may be set for one OSDU partition (<c>?partition=</c>), and a run bound to that
/// partition resolves with it first (docs/partitions-design.md section 5). Setting and removing are admin work; reading is
/// not, because what an estate delivers to is what every operator reading a record needs to know.
/// </para>
/// <para>
/// A value is a non-secret value or a <c>${env:...}</c> or <c>${keyvault:...}</c> reference the node resolves, so a
/// listing can show every value without showing a secret. What a property may hold is checked by
/// <see cref="DeliveryConfigStore"/> alone, as <c>sqlflow config</c> has it checked, and its refusals answer here as 400,
/// a repository the catalog does not hold as 404.
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

    /// <summary>
    /// Every property of every scope, with the repository each belongs to named; <c>repoId</c> narrows it to one repository's,
    /// <c>partition</c> to one partition's, and both to that repository's in that partition, as <c>sqlflow config list</c> does.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryConfigPropertyDto>>, ProblemHttpResult>> ListAsync(
        [FromQuery] Guid? repoId, [FromQuery] string? partition, CatalogDbContext db, DeliveryConfigStore config, CancellationToken ct)
    {
        try
        {
            var rows = await config.ListAsync(repoId, partition, ct).ConfigureAwait(false);
            return TypedResults.Ok(await DescribeAsync(db, rows, ct).ConfigureAwait(false));
        }
        catch (FlowValidationException invalid)
        {
            return Refused(invalid);
        }
    }

    /// <summary>
    /// What a run of a flow of this repository would be given: the control plane's properties with the repository's own
    /// over them, and for a run bound to <paramref name="partition"/>, the values set for that partition over both, which is
    /// what the node resolves against. The answer of the question an operator actually asks.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyDictionary<string, string>>, ProblemHttpResult>> EffectiveAsync(
        Guid repoId, [FromQuery] string? partition, DeliveryConfigStore config, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await config.EffectiveAsync(repoId, partition, ct).ConfigureAwait(false));
        }
        catch (FlowValidationException invalid)
        {
            return Refused(invalid);
        }
    }

    /// <summary>
    /// Sets a property for the control plane, or for one repository when <c>repoId</c> is given; for one partition when
    /// <c>partition</c> is given.
    /// </summary>
    private static async Task<Results<Ok<DeliveryConfigPropertyDto>, ProblemHttpResult>> SetAsync(
        string name,
        [FromQuery] Guid? repoId,
        [FromQuery] string? partition,
        DeliveryConfigSetRequest? request,
        CatalogDbContext db,
        DeliveryConfigStore config,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        try
        {
            var row = await config
                .SetAsync(repoId, partition, name, request?.Value, request?.Description, RequestActor.Label(user), clock.GetUtcNow().UtcDateTime, db, ct)
                .ConfigureAwait(false);
            var described = await DescribeAsync(db, [row], ct).ConfigureAwait(false);
            return TypedResults.Ok(described[0]);
        }
        catch (RepositoryNotRegisteredException missing)
        {
            return TypedResults.Problem(detail: missing.Message, statusCode: StatusCodes.Status404NotFound, title: "Repository not found");
        }
        catch (FlowValidationException invalid)
        {
            return Refused(invalid);
        }
    }

    /// <summary>
    /// Removes a property from the control plane, or from one repository when <c>repoId</c> is given; from one partition when
    /// <c>partition</c> is given. A repository is not looked up, so what was set for one since removed from the catalog can
    /// still be removed; a scope that holds no such property answers 404.
    /// </summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveAsync(
        string name, [FromQuery] Guid? repoId, [FromQuery] string? partition, DeliveryConfigStore config, CancellationToken ct)
    {
        try
        {
            return await config.RemoveAsync(repoId, partition, name, ct).ConfigureAwait(false)
                ? TypedResults.NoContent()
                : TypedResults.NotFound();
        }
        catch (FlowValidationException invalid)
        {
            return Refused(invalid);
        }
    }

    /// <summary>What the store refused, as the problem a caller reads: its message is the reason, naming the value at fault.</summary>
    private static ProblemHttpResult Refused(FlowValidationException invalid)
        => TypedResults.Problem(detail: invalid.Message, statusCode: StatusCodes.Status400BadRequest, title: "Configuration property refused");

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
                r.Partition,
                r.Description,
                r.UpdatedUtc,
                r.UpdatedBy)),
        ];
    }
}
