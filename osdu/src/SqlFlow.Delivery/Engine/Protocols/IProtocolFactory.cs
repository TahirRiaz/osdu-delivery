using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>Builds the protocol a flow declares. A seam: tests and stubs replace it without touching the engine.</summary>
public interface IProtocolFactory
{
    /// <summary>
    /// The protocol <paramref name="flow"/> declares, over <paramref name="http"/>. What the protocol says while it works
    /// (a record written again with its bulk link, a session read back, a workflow's status) goes to
    /// <paramref name="loggers"/>, which for a run are the run's own log and live trace.
    /// </summary>
    Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default);
}

public sealed class DefaultProtocolFactory : IProtocolFactory
{
    private readonly ISecretResolver _secrets;

    public DefaultProtocolFactory(ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
        => ProtocolFactory.CreateAsync(flow, http, _secrets, loggers, ct);
}
