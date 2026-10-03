using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>What the explorer asks the queries of: an element of a record, by the path the record inspector names it by, and its value.</summary>
/// <param name="Kind">The record's kind.</param>
/// <param name="Path">The element's path in the record, a list's items by their place: <c>data.GeoContexts[1].GeoTypeID</c>.</param>
/// <param name="Section">Whether the element is a section (an object, a list, an item of a list) rather than a value.</param>
/// <param name="Value">The value the record holds there, for a value: a text, a number, a boolean, or null.</param>
public sealed record DeliveryExplorerElementRequest(string? Kind, string? Path, bool? Section = null, JsonElement? Value = null);

public static partial class DeliveryExplorerEndpoints
{
    private static void MapElementQueryEndpoints(RouteGroupBuilder delivery)
        => delivery.MapPost("/explorer/element-queries", ElementQueriesAsync).WithName("ExploreDeliveryOsduElementQueries");

    /// <summary>
    /// The Lucene queries that find records by an element of a record (osdu/docs/explorer.md, The query of an element): how
    /// the index holds it, by the saved template of the record's kind, each query written as the module's own searches are
    /// and said in words, or why none finds it. Read from the saved templates alone; nothing is asked of OSDU.
    /// </summary>
    private static async Task<Results<Ok<ExplorerElementAnswer>, ProblemHttpResult>> ElementQueriesAsync(
        DeliveryExplorerElementRequest? body, ITemplateStore templates, CancellationToken ct)
    {
        if (body is null)
        {
            return DeliveryEndpoints.Invalid("Send the element's kind, path and value.");
        }

        var request = new ExplorerElementRequest
        {
            Kind = body.Kind?.Trim() ?? string.Empty,
            Path = body.Path?.Trim() ?? string.Empty,
            Section = body.Section ?? false,
            Value = body.Value is { ValueKind: JsonValueKind.Undefined } ? null : body.Value,
        };
        if (request.Problem() is { } problem)
        {
            return DeliveryEndpoints.Invalid(problem);
        }

        return TypedResults.Ok(await ExplorerElementQueries.DescribeAsync(request, templates, ct).ConfigureAwait(false));
    }
}
