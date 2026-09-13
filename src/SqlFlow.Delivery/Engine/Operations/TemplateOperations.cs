using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-schema-search</c>: searches the schemas OSDU publishes (openapi schema_service, <c>GET /schema</c>) through
/// the flow's OSDU connection, for the Templates page. Arguments: <c>authority</c>, <c>source</c>, <c>entityType</c>,
/// <c>status</c>, <c>latestVersion</c> (true unless <c>false</c>), <c>limit</c> and <c>offset</c>.
/// </summary>
public sealed class SearchSchemasOperation : DeliveryOperation
{
    public const string OperationName = "delivery-schema-search";

    public SearchSchemasOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var query = new OsduSchemaQuery
        {
            Authority = payload.Argument("authority"),
            Source = payload.Argument("source"),
            EntityType = payload.Argument("entityType"),
            Status = payload.Argument("status"),
            LatestVersion = !string.Equals(payload.Argument("latestVersion"), "false", StringComparison.OrdinalIgnoreCase),
            Limit = Number(payload, "limit", OsduSchemaQuery.MaxLimit),
            Offset = Number(payload, "offset", 0),
        };

        using var correlation = OsduCorrelation.Begin();
        using var osdu = await OsduConnection.CreateAsync(
            flow.Target.Endpoint, flow.Target.Auth, flow.Target.Headers, flow.Reliability, Context.Secrets, allowLoopback: EngineContext.LoopbackAllowed, ct: ct).ConfigureAwait(false);
        var found = await TemplateSources.SearchAsync(osdu, query, ct).ConfigureAwait(false);
        return new
        {
            flow = flow.Name,
            endpoint = flow.Target.Endpoint,
            correlationId = correlation.Id,
            schemas = found.Schemas,
            offset = found.Offset,
            count = found.Count,
            totalCount = found.TotalCount,
        };
    }

    private static int Number(ComputeTaskPayload payload, string name, int fallback)
    {
        var text = payload.Argument(name);
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SqlFlowException($"The '{OperationName}' operation's '{name}' argument '{text}' is not a whole number.");
    }
}

/// <summary>
/// <c>delivery-schema-fetch</c>: fetches one kind's schema from OSDU (openapi schema_service, <c>GET /schema/{id}</c>) and every
/// schema it refers to, through the flow's OSDU connection, and returns it bundled. Nothing is saved: the Templates page
/// shows the schema as a template, and saving stores exactly what was shown. Argument: <c>kind</c>.
/// </summary>
public sealed class FetchSchemaOperation : DeliveryOperation
{
    public const string OperationName = "delivery-schema-fetch";

    public FetchSchemaOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    protected override async Task<object> RunAsync(FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var kind = payload.RequireArgument("kind");
        using var correlation = OsduCorrelation.Begin();
        using var osdu = await OsduConnection.CreateAsync(
            flow.Target.Endpoint, flow.Target.Auth, flow.Target.Headers, flow.Reliability, Context.Secrets, allowLoopback: EngineContext.LoopbackAllowed, ct: ct).ConfigureAwait(false);
        var schema = await TemplateSources.FetchAsync(osdu, kind, Context.Time, ct).ConfigureAwait(false);
        return new
        {
            flow = flow.Name,
            endpoint = flow.Target.Endpoint,
            correlationId = correlation.Id,
            kind = schema.Kind,
            version = schema.Version,
            schema = schema.Root,
        };
    }
}
