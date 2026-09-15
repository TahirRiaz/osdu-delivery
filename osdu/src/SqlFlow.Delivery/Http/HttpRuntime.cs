using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Composes the per-flow HTTP stack the way SQLFlow's engine does: one client, one executor for data, one for
/// auth (with an empty allowlist, since token endpoints live on identity providers), and the auth resolver.
/// </summary>
public sealed class HttpRuntime : IDisposable
{
    private readonly HttpClient _client;

    public HttpRuntime(FlowReliability reliability, ISecretResolver secrets, TimeProvider? time = null, HttpMessageHandler? handler = null, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(secrets);
        var clock = time ?? TimeProvider.System;
        _client = handler is null
            ? HttpClientBuilder.Build(reliability)
            : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(reliability.TimeoutSeconds) };

        Retry = new RetryPolicy(reliability.Retry, clock);
        Data = new HttpExecutor(_client, Retry, new RateLimiter(reliability.RateLimitRps, clock), new UrlGuard(reliability.UrlAllowlist, allowLoopback), reliability.MaxResponseBytes, clock);
        Auth = new HttpExecutor(_client, Retry, new RateLimiter(0, clock), new UrlGuard([], allowLoopback), 1024 * 1024, clock);
        AuthResolver = new AuthResolver(secrets, clock);
    }

    public HttpExecutor Data { get; }

    public HttpExecutor Auth { get; }

    public AuthResolver AuthResolver { get; }

    public RetryPolicy Retry { get; }

    public void Dispose() => _client.Dispose();
}
