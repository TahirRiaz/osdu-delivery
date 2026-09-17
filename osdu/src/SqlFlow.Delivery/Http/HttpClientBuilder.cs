// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/HttpClientBuilder.cs. Namespace changed; configuration record is the flow's
// FlowReliability; the user agent names this project; redirects are followed by HttpExecutor, which checks each hop, and
// every address a connection opens to is checked against the deployment's NetworkPolicy.
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// Builds the per-flow <see cref="HttpClient"/>: the configured timeout, TLS verification toggle, keep-alive and a stable
/// user agent, with no redirects of its own (<see cref="HttpExecutor"/> follows them, checking each hop). Every connection
/// opens only to an address the <see cref="NetworkPolicy"/> reaches, whatever name led to it. One client per run.
/// </summary>
public static class HttpClientBuilder
{
    private static readonly string UserAgent = $"sqlflow-delivery/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1"}";

    public static HttpClient Build(FlowReliability reliability, NetworkPolicy network)
    {
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(network);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(reliability.TimeoutSeconds, 30)),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            ConnectCallback = (context, ct) => KeepAliveConnectAsync(context, network, ct),
        };

        if (!reliability.VerifyTls)
        {
            // Deliberate opt-in gated behind the flow's explicit verifyTls: false, for targets with a self-signed or
            // enterprise-internal certificate.
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

    /// <summary>
    /// Opens each new pooled connection on a socket with TCP keepalive enabled, one socket per candidate address
    /// (a dual-stack socket that failed its first connect cannot be reused on Linux). Only the addresses
    /// <paramref name="network"/> reaches are tried, and the socket connects to the address that was checked, so a name that
    /// resolves somewhere else a moment later cannot move the connection. Behind an HTTP proxy the address checked is the
    /// proxy's, since the proxy resolves the target.
    /// </summary>
    internal static async ValueTask<Stream> KeepAliveConnectAsync(SocketsHttpConnectionContext context, NetworkPolicy network, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var resolved = IPAddress.TryParse(endPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);
        var addresses = Reachable(endPoint.Host, resolved, network);

        for (var i = 0; i < addresses.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var socket = CreateKeepAliveSocket(addresses[i].AddressFamily);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addresses[i], endPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) when (i < addresses.Length - 1)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>The addresses of <paramref name="host"/> the nodes may connect to; none is a refusal naming each address and why.</summary>
    internal static IPAddress[] Reachable(string host, IReadOnlyList<IPAddress> resolved, NetworkPolicy network)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(network);
        var reachable = new List<IPAddress>(resolved.Count);
        var refused = new List<string>();
        foreach (var address in resolved)
        {
            if (network.Refusal(address) is { } refusal)
            {
                refused.Add($"{address} ({refusal})");
            }
            else
            {
                reachable.Add(address);
            }
        }

        if (reachable.Count == 0)
        {
            throw new UrlRefusedException(refused.Count == 0
                ? $"'{host}' resolves to no address."
                : $"'{host}' resolves only to addresses the delivery nodes may not reach: {string.Join("; ", refused)}.");
        }

        return [.. reachable];
    }

    private static Socket CreateKeepAliveSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        return socket;
    }
}
