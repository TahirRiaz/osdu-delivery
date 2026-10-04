using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A record the explorer checks against its schema.</summary>
/// <param name="TargetId">The record's id.</param>
/// <param name="Version">The version to check; its latest when left out.</param>
/// <param name="Schema"><c>osdu</c> (the default): what the partition's Schema service holds for its kind; <c>saved</c>: a saved template.</param>
/// <param name="TemplateVersion">With <c>saved</c>, the template version to check against; the kind's newest when left out.</param>
public sealed record DeliveryExplorerValidateRequest(string? TargetId, long? Version = null, string? Schema = null, string? TemplateVersion = null);

/// <summary>The records a search finds, checked against their schemas: the search as the explorer sends it, how many records at most, and the schema.</summary>
/// <param name="Search">The search, as <c>/explorer/search</c> takes it; its page and grouping are not used.</param>
/// <param name="Max">The most records read and checked, 1 to 1,000; 1,000 when left out.</param>
/// <param name="Schema"><c>osdu</c> (the default) or <c>saved</c>, each kind's newest saved template.</param>
public sealed record DeliveryExplorerValidateListRequest(DeliveryExplorerSearchRequest? Search = null, int? Max = null, string? Schema = null);

public static partial class DeliveryExplorerEndpoints
{
    private static void MapValidateEndpoints(RouteGroupBuilder delivery)
    {
        delivery.MapPost("/explorer/validate", ValidateAsync).WithName("ExploreDeliveryOsduValidate");
        delivery.MapPost("/explorer/validate-list", ValidateListAsync).WithName("ExploreDeliveryOsduValidateList");
    }

    /// <summary>
    /// One record OSDU holds checked against the schema of its kind (osdu/docs/explorer.md, Validate): the Schema service's,
    /// or a saved template's; the records it refers to are looked up in storage. Reads OSDU through the partition's connection,
    /// as every explorer read does.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ValidateAsync(
        DeliveryExplorerValidateRequest? body, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var read = new DeliveryReadRequest(body?.TargetId, body?.Version);
        if (DeliveryEndpoints.TargetProblem(read, out var asked) is { } invalid)
        {
            return invalid;
        }

        if (SchemaProblem(body!.Schema) is { } schemaProblem)
        {
            return DeliveryEndpoints.Invalid(schemaProblem);
        }

        if (body.TemplateVersion is { } version && (version.Trim().Length is 0 or > 64 || version.Any(char.IsControl)))
        {
            return DeliveryEndpoints.Invalid("Name the saved template version as the Templates page shows it.");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["targetId"] = TargetId.WithoutVersion(asked) };
        DeliveryEndpoints.WithVersion(arguments, read);
        if (!string.IsNullOrWhiteSpace(body.Schema))
        {
            arguments[ExploreOperation.SchemaArgument] = body.Schema.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(body.TemplateVersion))
        {
            arguments[ExploreOperation.TemplateVersionArgument] = body.TemplateVersion.Trim();
        }

        return await QueueAsync(ExploreOperation.ValidateAction, arguments, partition, db, documents, partitions, ledger, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The records a search finds, up to 1,000, checked against their schemas and counted by the rules they break, with an
    /// example of each (osdu/docs/explorer.md, Validate these records).
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ValidateListAsync(
        DeliveryExplorerValidateListRequest? body, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var asked = body ?? new DeliveryExplorerValidateListRequest();
        var (search, invalid) = SearchOf((asked.Search ?? new DeliveryExplorerSearchRequest()) with { Offset = null, Limit = null, Facet = null });
        if (search is null)
        {
            return DeliveryEndpoints.Invalid(invalid!);
        }

        if (asked.Max is { } max && max is < 1 or > ExplorerChecks.MaxRecords)
        {
            return DeliveryEndpoints.Invalid(string.Create(CultureInfo.InvariantCulture, $"A check reads 1 to {ExplorerChecks.MaxRecords} records; {max} is not that."));
        }

        if (SchemaProblem(asked.Schema) is { } schemaProblem)
        {
            return DeliveryEndpoints.Invalid(schemaProblem);
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExploreOperation.SearchArgument] = search.ToJson(),
            [ExploreOperation.MaxArgument] = (asked.Max ?? ExplorerChecks.MaxRecords).ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(asked.Schema))
        {
            arguments[ExploreOperation.SchemaArgument] = asked.Schema.Trim().ToLowerInvariant();
        }

        return await QueueAsync(ExploreOperation.ValidateListAction, arguments, partition, db, documents, partitions, ledger, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>Why a request's schema is not one a record is checked against, or null when it is (or is left out).</summary>
    private static string? SchemaProblem(string? schema)
        => string.IsNullOrWhiteSpace(schema) || schema.Trim().ToLowerInvariant() is "osdu" or "saved"
            ? null
            : $"'{schema}' is not a schema a record is checked against: osdu (what the Schema service holds) or saved (a saved template).";
}
