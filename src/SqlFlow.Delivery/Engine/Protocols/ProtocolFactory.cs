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

        var endpoint = await secrets.ResolveAsync(flow.Target.Endpoint, ct).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in flow.Target.Headers)
        {
            headers[name] = await secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        var client = new OsduHttpClient(http, endpoint, flow.Target.Auth, headers);
        return flow.Target.Protocol switch
        {
            DeliveryProtocol.OsduRecord => new OsduRecordProtocol(client, flow.Target.ProtocolOptions),
            DeliveryProtocol.OsduWellLog => new OsduWellLogProtocol(client, flow.Target.ProtocolOptions, loggers.CreateLogger<OsduWellLogProtocol>(), flow.Reliability.MaxRequestBodyBytes),
            _ => throw new FlowValidationException(
                $"{flow.SourcePath ?? flow.Name}: target.protocol '{flow.Target.Protocol}' is declared in the vocabulary but not implemented in this version."),
        };
    }
}
