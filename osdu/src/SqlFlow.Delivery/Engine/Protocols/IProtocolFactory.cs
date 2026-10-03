using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

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

/// <summary>
/// The protocols the engine delivers through. Every reference the protocol reaches the target with (the endpoint, the headers,
/// the credentials of the services behind it) is resolved by the resolver of the runtime it is given
/// (<see cref="HttpRuntime.Secrets"/>), which applies the central configuration the run or operation was given ahead of the
/// process's environment. A protocol therefore reaches the endpoint the run's record searches reach, in the partition the run was
/// bound to, and never one the process's environment names instead.
/// </summary>
public sealed class DefaultProtocolFactory : IProtocolFactory
{
    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        return ProtocolFactory.CreateAsync(flow, http, http.Secrets, loggers, ct);
    }
}
