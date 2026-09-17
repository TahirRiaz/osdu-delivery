using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>Builds the named protocol a flow declares (design.md section 8.4).</summary>
public static class ProtocolFactory
{
    public static async Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ISecretResolver secrets, ILoggerFactory loggers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(loggers);

        var client = await ClientAsync(http, flow.Target.Endpoint, flow.Target.Auth, flow.Target.Headers, secrets, ct).ConfigureAwait(false);
        var options = flow.Target.ProtocolOptions;
        var ceiling = flow.Reliability.MaxRequestBodyBytes;
        return flow.Target.Protocol switch
        {
            DeliveryProtocol.OsduRecord => new OsduRecordProtocol(client, options),
            // The DDMSs the flow names by registration are read here, so every operation routes by what they registered.
            DeliveryProtocol.OsduWellLog => new OsduWellLogProtocol(
                client, options, loggers.CreateLogger<OsduWellLogProtocol>(), ceiling, routing: await RoutingAsync(flow, client, ct).ConfigureAwait(false)),
            DeliveryProtocol.OsduFile => new OsduFileProtocol(client, options, ceiling),
            DeliveryProtocol.OsduDataset => new OsduDatasetProtocol(client, options, loggers.CreateLogger<OsduDatasetProtocol>(), ceiling),
            DeliveryProtocol.OsduManifest => new OsduManifestProtocol(client, options, loggers.CreateLogger<OsduManifestProtocol>(), ceiling),
            DeliveryProtocol.OsduFileAndDdms => new OsduFileAndDdmsProtocol(
                client, options, loggers.CreateLogger<OsduFileAndDdmsProtocol>(), ceiling, routing: await RoutingAsync(flow, client, ct).ConfigureAwait(false)),
            DeliveryProtocol.OsduManifestAndDdms => new OsduManifestAndDdmsProtocol(
                client, options, loggers.CreateLogger<OsduManifestAndDdmsProtocol>(), ceiling, routing: await RoutingAsync(flow, client, ct).ConfigureAwait(false)),
            DeliveryProtocol.OsduWorkflow => new OsduWorkflowProtocol(
                client,
                options,
                flow.Target.Workflow ?? throw new FlowValidationException($"{flow.SourcePath ?? flow.Name}: the workflow route runs the workflow the flow declares, and it declares none."),
                loggers.CreateLogger<OsduWorkflowProtocol>(),
                secrets,
                flow.Target.Airflow is { } airflow ? new Workflows.AirflowXCom(http, airflow, secrets) : new Workflows.LatestInfoXCom(client),
                ceiling),
            DeliveryProtocol.OsduDspdm => new OsduDspdmProtocol(client, options, flow.Target.Dspdm, loggers.CreateLogger<OsduDspdmProtocol>()),
            _ => throw new FlowValidationException($"{flow.SourcePath ?? flow.Name}: target.protocol '{flow.Target.Protocol}' is not a known protocol."),
        };
    }

    /// <summary>Where a route that reaches a DDMS sends each record, with the registrations the flow names read.</summary>
    private static async Task<DdmsRouting> RoutingAsync(FlowDefinition flow, OsduHttpClient client, CancellationToken ct)
        => DdmsRouting.Of(await DdmsDiscovery.ResolveAsync(flow, client, ct).ConfigureAwait(false));

    /// <summary>The OSDU client over a declared endpoint, auth and headers, every secret reference resolved here and nowhere else.</summary>
    public static async Task<OsduHttpClient> ClientAsync(HttpRuntime http, string endpoint, TargetAuth auth, IReadOnlyDictionary<string, string> headers, ISecretResolver secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(secrets);
        var resolvedEndpoint = await secrets.ResolveAsync(endpoint, ct).ConfigureAwait(false);
        var resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            resolvedHeaders[name] = await secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        return new OsduHttpClient(http, resolvedEndpoint, auth, resolvedHeaders);
    }
}
