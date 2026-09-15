using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>Builds the protocol a flow declares. A seam: tests and stubs replace it without touching the engine.</summary>
public interface IProtocolFactory
{
    Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, CancellationToken ct = default);
}

public sealed class DefaultProtocolFactory : IProtocolFactory
{
    private readonly ISecretResolver _secrets;
    private readonly ILoggerFactory _loggers;

    public DefaultProtocolFactory(ISecretResolver secrets, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(loggers);
        _secrets = secrets;
        _loggers = loggers;
    }

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, CancellationToken ct = default)
        => ProtocolFactory.CreateAsync(flow, http, _secrets, _loggers, ct);
}
