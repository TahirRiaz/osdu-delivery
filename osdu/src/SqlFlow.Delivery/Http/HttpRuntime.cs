using System.Net;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Composes the per-flow HTTP stack the way SQLFlow's engine does: one client, one executor for data, one for
/// auth (with an empty allowlist, since token endpoints live on identity providers), and the auth resolver. Both reach only
/// the addresses the deployment's <see cref="NetworkPolicy"/> allows.
/// </summary>
public sealed class HttpRuntime : IDisposable
{
    private readonly HttpClient _client;

    /// <param name="reliability">The flow's reliability settings.</param>
    /// <param name="secrets">Resolves the secret references auth names.</param>
    /// <param name="time">The clock; the system clock when null.</param>
    /// <param name="handler">A handler to send through instead of the network (the tests).</param>
    /// <param name="allowLoopback">Whether loopback addresses are reachable.</param>
    /// <param name="privateNetworks">The private ranges the deployment reaches; the ones it lists under <see cref="NetworkPolicy.PrivateNetworksVariable"/> when null.</param>
    public HttpRuntime(
        FlowReliability reliability, ISecretResolver secrets, TimeProvider? time = null, HttpMessageHandler? handler = null, bool allowLoopback = false,
        IReadOnlyList<IPNetwork>? privateNetworks = null)
    {
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(secrets);
        var clock = time ?? TimeProvider.System;
        Network = new NetworkPolicy
        {
            AllowLoopback = allowLoopback,
            PrivateNetworks = privateNetworks ?? NetworkPolicy.PrivateNetworksFromEnvironment(),
        };
        _client = handler is null
            ? HttpClientBuilder.Build(reliability, Network)
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(reliability.TimeoutSeconds) };

        Retry = new RetryPolicy(reliability.Retry, clock);
        Data = new HttpExecutor(_client, Retry, new RateLimiter(reliability.RateLimitRps, clock), new UrlGuard(reliability.UrlAllowlist, Network), reliability.MaxResponseBytes, clock);
        Auth = new HttpExecutor(_client, Retry, new RateLimiter(0, clock), new UrlGuard([], Network), 1024 * 1024, clock);
        AuthResolver = new AuthResolver(secrets, clock);
    }

    /// <summary>The addresses this stack reaches.</summary>
    public NetworkPolicy Network { get; }

    /// <summary>
    /// The invoker every request of this flow goes through. The ETP route's WebSocket upgrade is sent with it, so a
    /// <c>wss</c> connection opens under exactly the same address policy, TLS setting and connect timeout as the
    /// flow's HTTP calls, rather than under a second stack of its own.
    /// </summary>
    public HttpMessageInvoker Invoker => _client;

    public HttpExecutor Data { get; }

    public HttpExecutor Auth { get; }

    public AuthResolver AuthResolver { get; }

    public RetryPolicy Retry { get; }

    public void Dispose() => _client.Dispose();
}
