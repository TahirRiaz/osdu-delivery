using System.Net;
using System.Reflection;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Runtime;

/// <summary>Builds the per-flow <see cref="HttpClient"/>: the configured timeout, TLS verification toggle, a bounded
/// redirect policy, and a stable user agent. One client per run (its handler carries the flow's TLS/timeout).</summary>
public static class HttpClientBuilder
{
    private static readonly string UserAgent = $"SqlFlow-Acquire/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0"}";

    public static HttpClient Build(AcquireReliability reliability)
    {
        ArgumentNullException.ThrowIfNull(reliability);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(reliability.TimeoutSeconds, 30)),
        };

        if (!reliability.VerifyTls)
        {
            // Deliberate opt-in, gated behind the flow's explicit VerifyTls: false, for targets with a self-signed
            // or enterprise-internal certificate. Accepting any certificate here is the requested behavior, not a
            // lapse, so CA5359 is suppressed for this one assignment.
#pragma warning disable CA5359
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(reliability.TimeoutSeconds),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }
}
