// Vendored from SQLFlow (https://github.com/TahirRiaz/sqlflow-v3, commit ddd4ea12160bda044f75dcad2bbec5099c3a7263)
// src/SqlFlow.Acquire/Runtime/UrlGuard.cs. Namespace and exception type changed; loopback may be allowed explicitly
// so a developer can point a flow at a local stub.
using System.Net;
using System.Net.Sockets;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// SSRF guard for outbound delivery requests. A URL must be http/https with a host; a literal-IP host in a private,
/// loopback, link-local (including the cloud metadata address 169.254.169.254), unspecified, multicast or
/// unique-local range is rejected unless <c>allowLoopback</c> is set for loopback; and when an allowlist is
/// configured the host must match it (*.suffix matches the bare domain and any subdomain).
/// </summary>
public sealed class UrlGuard
{
    private readonly IReadOnlyList<string> _allowlist;
    private readonly bool _allowLoopback;

    public UrlGuard(IReadOnlyList<string> allowlist, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        _allowlist = allowlist;
        _allowLoopback = allowLoopback;
    }

    public void Check(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme is not ("http" or "https"))
        {
            throw new DeliveryException($"URL '{url}' uses scheme '{url.Scheme}'; only http and https are allowed.");
        }

        var host = url.Host;
        if (string.IsNullOrEmpty(host))
        {
            throw new DeliveryException($"URL '{url}' has no host.");
        }

        if (_allowLoopback && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var lo) && IPAddress.IsLoopback(lo))))
        {
            return;
        }

        if (IPAddress.TryParse(host, out var ip) && IsDangerous(ip))
        {
            throw new DeliveryException($"URL '{url}' targets a blocked address range ({ip}). Private, loopback, link-local and metadata addresses are not reachable.");
        }

        if (_allowlist.Count > 0 && !IsAllowed(host))
        {
            throw new DeliveryException(
                $"Host '{host}' is not in the url allowlist ({string.Join(", ", _allowlist)}). Add it to reliability.urlAllowlist to permit the request.");
        }
    }

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

    private static bool IsDangerous(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when b[1] == 254 => true,
                172 when b[1] >= 16 && b[1] <= 31 => true,
                192 when b[1] == 168 => true,
                >= 224 => true,
                _ => false,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            {
                return true;
            }

            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            if (ip.IsIPv4MappedToIPv6 && IsDangerous(ip.MapToIPv4()))
            {
                return true;
            }
        }

        return false;
    }
}
