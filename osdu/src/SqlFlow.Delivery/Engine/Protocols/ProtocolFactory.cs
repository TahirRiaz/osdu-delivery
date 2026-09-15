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
        return flow.Target.Protocol switch
        {
            DeliveryProtocol.OsduRecord => new OsduRecordProtocol(client, flow.Target.ProtocolOptions),
            DeliveryProtocol.OsduWellLog => new OsduWellLogProtocol(client, flow.Target.ProtocolOptions, loggers.CreateLogger<OsduWellLogProtocol>(), flow.Reliability.MaxRequestBodyBytes),
            DeliveryProtocol.OsduFile => new OsduFileProtocol(client, flow.Target.ProtocolOptions, flow.Reliability.MaxRequestBodyBytes),
            DeliveryProtocol.OsduManifest => new OsduManifestProtocol(client, flow.Target.ProtocolOptions, loggers.CreateLogger<OsduManifestProtocol>(), flow.Reliability.MaxRequestBodyBytes),
            _ => throw new FlowValidationException($"{flow.SourcePath ?? flow.Name}: target.protocol '{flow.Target.Protocol}' is not a known protocol."),
        };
    }

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
