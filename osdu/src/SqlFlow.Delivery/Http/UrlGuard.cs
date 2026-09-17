// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/UrlGuard.cs. Namespace and exception type changed; loopback may be allowed explicitly
// so a developer can point a flow at a local stub; the address rules are the deployment's NetworkPolicy, applied to the
// addresses names resolve to as well (HttpClientBuilder), and every redirect hop is checked (HttpExecutor).
using System.Net;

namespace SqlFlow.Delivery.Http;

/// <summary>A request the URL guard refused: the scheme, the address or the host is not one the delivery nodes may reach.</summary>
public sealed class UrlRefusedException : DeliveryException
{
    public UrlRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// SSRF guard for outbound delivery requests. A URL must be http/https with a host; an address written as the host must
/// be one the deployment's <see cref="NetworkPolicy"/> reaches (never link-local or metadata addresses; loopback and
/// private ranges only when allowed); and when an allowlist is configured the host must match it (*.suffix matches the
/// bare domain and any subdomain). The addresses a host name resolves to are checked against the same policy when the
/// connection opens, and a redirect is followed only to a URL this guard lets through, never from https to http.
/// </summary>
public sealed class UrlGuard
{
    private readonly IReadOnlyList<string> _allowlist;

    public UrlGuard(IReadOnlyList<string> allowlist, bool allowLoopback = false)
        : this(allowlist, new NetworkPolicy { AllowLoopback = allowLoopback })
    {
    }

    public UrlGuard(IReadOnlyList<string> allowlist, NetworkPolicy network)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(network);
        _allowlist = allowlist;
        Network = network;
    }

    /// <summary>The addresses the guard lets requests reach.</summary>
    public NetworkPolicy Network { get; }

    public void Check(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            throw new UrlRefusedException($"URL '{Describe(url)}' uses scheme '{(url.IsAbsoluteUri ? url.Scheme : "(none)")}'; only http and https are allowed.");
        }

        var host = url.Host;
        if (string.IsNullOrEmpty(host))
        {
            throw new UrlRefusedException($"URL '{Describe(url)}' has no host.");
        }

        // A name that means loopback everywhere is refused here, before anything resolves it.
        if (!Network.AllowLoopback && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UrlRefusedException($"URL '{Describe(url)}' targets '{host}', a loopback address, which only a process with SQLFLOW_DELIVERY_ALLOW_LOOPBACK reaches.");
        }

        if (IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var ip) && Network.Refusal(ip) is { } refusal)
        {
            throw new UrlRefusedException($"URL '{Describe(url)}' targets {ip}, {refusal}.");
        }

        if (_allowlist.Count > 0 && !IsAllowed(host))
        {
            throw new UrlRefusedException(
                $"Host '{host}' is not in the url allowlist ({string.Join(", ", _allowlist)}). Add it to reliability.urlAllowlist to permit the request.");
        }
    }

    /// <summary>
    /// Checks a redirect from <paramref name="from"/> to <paramref name="to"/>: the target passes <see cref="Check"/>, and a
    /// redirect never leaves https for http.
    /// </summary>
    public void CheckRedirect(Uri from, Uri to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Scheme == Uri.UriSchemeHttps && to.IsAbsoluteUri && to.Scheme == Uri.UriSchemeHttp)
        {
            throw new UrlRefusedException($"{Describe(from)} redirected to {Describe(to)}; a redirect from https to http is refused.");
        }

        Check(to);
    }

    /// <summary>A URL as a message names it: without its query string, where a signed URL carries its credential.</summary>
    internal static string Describe(Uri url) => url.IsAbsoluteUri ? url.GetLeftPart(UriPartial.Path) : url.OriginalString.Split('?')[0];

    private bool IsAllowed(string host)
    {
        foreach (var pattern in _allowlist)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[2..];
                if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
