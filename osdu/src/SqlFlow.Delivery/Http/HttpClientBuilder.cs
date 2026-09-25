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

    /// <summary>The environment switch that lets a flow of this deployment turn TLS verification off.</summary>
    public const string AllowInsecureTlsVariable = "SQLFLOW_DELIVERY_ALLOW_INSECURE_TLS";

    /// <summary>Whether a flow may turn TLS verification off in this process (the switch above, read at each build).</summary>
    public static bool InsecureTlsAllowed
        => Environment.GetEnvironmentVariable(AllowInsecureTlsVariable) is { } value && value.Equals("true", StringComparison.OrdinalIgnoreCase);

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
            // A flow asking for an unverified certificate is not enough on its own: a document is a repository file, and
            // one that turned verification off would otherwise take every node that ran it off TLS without the
            // deployment agreeing. The deployment agrees by setting the switch, as it does for loopback and private
            // ranges; without it the run is refused here rather than sent over a connection nobody checked.
            if (!InsecureTlsAllowed)
            {
                throw new UrlRefusedException(
                    "This flow declares reliability.verifyTls: false, and this deployment verifies every certificate. "
                    + $"A deployment that has to reach a target with a self-signed or enterprise-internal certificate sets {AllowInsecureTlsVariable}=true "
                    + "on the nodes that reach it; the honest fix is to trust the issuing authority on those nodes instead.");
            }

            // Deliberate opt-in, gated behind the flow's explicit verifyTls: false and the deployment's switch, for
            // targets with a self-signed or enterprise-internal certificate.
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
    /// proxy's, since the proxy resolves the target. The addresses are raced the Happy Eyeballs way (RFC 8305): families
    /// interleaved, each attempt given <see cref="ConnectionAttemptDelay"/> alone before the next starts beside it, so an
    /// address family the network silently drops (IPv6 assigned but not routed) cannot spend the whole connect timeout.
    /// </summary>
    internal static async ValueTask<Stream> KeepAliveConnectAsync(SocketsHttpConnectionContext context, NetworkPolicy network, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var resolved = IPAddress.TryParse(endPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);
        var addresses = Interleave(Reachable(endPoint.Host, resolved, network));

        var socket = await RaceAsync(
            endPoint.Host, addresses, (address, token) => ConnectAsync(address, endPoint.Port, token), ConnectionAttemptDelay, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>How long a connection attempt runs alone before the next address is tried beside it (RFC 8305, section 5).</summary>
    internal static readonly TimeSpan ConnectionAttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Orders <paramref name="addresses"/> alternating between the family of the first one and the other (RFC 8305,
    /// section 4), keeping the resolver's order within each family.
    /// </summary>
    internal static IPAddress[] Interleave(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count < 2)
        {
            return [.. addresses];
        }

        var family = addresses[0].AddressFamily;
        var preferred = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == family));
        var other = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily != family));
        var ordered = new IPAddress[addresses.Count];
        for (var i = 0; i < ordered.Length; i++)
        {
            ordered[i] = (i % 2 == 0 && preferred.Count > 0) || other.Count == 0 ? preferred.Dequeue() : other.Dequeue();
        }

        return ordered;
    }

    /// <summary>
    /// Starts an attempt on the first address, and one on the next each time <paramref name="attemptDelay"/> passes or an
    /// attempt fails, until one connects; the others are cancelled and anything they still open is disposed. Every
    /// address failing is a <see cref="SocketException"/> naming each one and why; <paramref name="ct"/> (the handler's
    /// connect timeout) cancels the whole race.
    /// </summary>
    internal static async Task<T> RaceAsync<T>(
        string host, IReadOnlyList<IPAddress> addresses, Func<IPAddress, CancellationToken, Task<T>> attempt, TimeSpan attemptDelay, CancellationToken ct)
        where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(attempt);
        if (addresses.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound, $"'{host}' resolves to no address.");
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new Dictionary<Task<T>, IPAddress>();
        var failures = new List<string>(addresses.Count);
        var lastError = SocketError.HostUnreachable;
        var next = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (next < addresses.Count)
                {
                    var address = addresses[next++];
                    running.Add(attempt(address, race.Token), address);
                }

                if (running.Count == 0)
                {
                    break;
                }

                var waitingOn = running.Keys.Cast<Task>();
                if (next < addresses.Count)
                {
                    waitingOn = waitingOn.Append(Task.Delay(attemptDelay, race.Token));
                }

                if (await Task.WhenAny(waitingOn).ConfigureAwait(false) is not Task<T> finished || !running.Remove(finished, out var tried))
                {
                    continue;
                }

                if (finished.Status == TaskStatus.RanToCompletion)
                {
                    return finished.Result;
                }

                ct.ThrowIfCancellationRequested();
                var error = finished.Exception?.GetBaseException();
                if (error is SocketException socketError)
                {
                    lastError = socketError.SocketErrorCode;
                }

                failures.Add($"{tried} ({error?.Message ?? "cancelled"})");
            }
        }
        finally
        {
            await race.CancelAsync().ConfigureAwait(false);
            foreach (var loser in running.Keys)
            {
                // A losing attempt can still connect after the winner did; its connection is closed rather than leaked.
                _ = loser.ContinueWith(
                    static t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                        {
                            t.Result.Dispose();
                        }
                        else
                        {
                            _ = t.Exception;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        throw new SocketException((int)lastError, $"No address of '{host}' accepted a connection: {string.Join("; ", failures)}.");
    }

    private static async Task<Socket> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = CreateKeepAliveSocket(address.AddressFamily);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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
