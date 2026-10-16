using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SqlFlow.Delivery.Http;

/// <summary>
/// The addresses the delivery nodes may connect to. Public addresses are reachable. Loopback is reachable only when the
/// process allows it (a local OSDU stub, the tests). Private ranges (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7) are
/// reachable only when the deployment lists them (<see cref="PrivateNetworksVariable"/>): an OSDU or a storage account
/// behind Azure Private Link, or any target of a VNet-integrated environment, resolves to one. Link-local addresses (the
/// cloud metadata services answer on 169.254.169.254), unspecified, multicast and reserved addresses, the Azure platform's
/// wireserver and the other cloud metadata addresses are never reachable, whatever a deployment lists. The policy applies
/// to an address written in a URL and to every address a host name resolves to, so a name cannot reach what an address
/// written out could not.
/// </summary>
public sealed record NetworkPolicy
{
    /// <summary>The deployment setting that lists the private ranges the nodes may reach: CIDR ranges, comma separated.</summary>
    public const string PrivateNetworksVariable = "SQLFLOW_DELIVERY_PRIVATE_NETWORKS";

    private static readonly IPNetwork[] PrivateRanges =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fec0::/10"),
    ];

    /// <summary>Addresses no deployment reaches: the Azure wireserver, and the cloud metadata services outside link-local.</summary>
    private static readonly IPAddress[] PlatformAddresses =
    [
        IPAddress.Parse("168.63.129.16"),
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("fd00:ec2::254"),
    ];

    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");
    private static readonly ConcurrentDictionary<string, IReadOnlyList<IPNetwork>> Parsed = new(StringComparer.Ordinal);

    /// <summary>Whether loopback addresses are reachable.</summary>
    public bool AllowLoopback { get; init; }

    /// <summary>The private ranges the deployment reaches.</summary>
    public IReadOnlyList<IPNetwork> PrivateNetworks { get; init; } = [];

    /// <summary>The private ranges this process's deployment lists under <see cref="PrivateNetworksVariable"/>.</summary>
    public static IReadOnlyList<IPNetwork> PrivateNetworksFromEnvironment()
        => ParseNetworks(Environment.GetEnvironmentVariable(PrivateNetworksVariable), PrivateNetworksVariable);

    /// <summary>
    /// The ranges a setting lists: CIDR ranges (<c>10.20.0.0/16</c>, <c>fd12:3456::/48</c>) or single addresses, separated by
    /// commas, semicolons or white space. An entry that is neither is refused, naming the setting and the entry.
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseNetworks(string? text, string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return Parsed.GetOrAdd(text.Trim(), value =>
        {
            var networks = new List<IPNetwork>();
            foreach (var entry in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (IPNetwork.TryParse(entry, out var network))
                {
                    networks.Add(network);
                }
                else if (IPAddress.TryParse(entry, out var single))
                {
                    networks.Add(new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
                }
                else
                {
                    throw new DeliveryException(
                        $"{setting} lists '{entry}', which is not a CIDR range such as 10.20.0.0/16 or fd12:3456::/48, nor an address.");
                }
            }

            return networks;
        });
    }

    /// <summary>Why the nodes may not connect to <paramref name="address"/>, or null when they may.</summary>
    public string? Refusal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ip = Embedded(address);
        if (IPAddress.IsLoopback(ip))
        {
            return AllowLoopback ? null : "a loopback address, which only a process with SQLFLOW_DELIVERY_ALLOW_LOOPBACK reaches";
        }

        if (PlatformAddresses.Any(p => p.Equals(ip)))
        {
            return "an address of the cloud platform itself (its wireserver or metadata service), which no deployment reaches";
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            switch (b[0])
            {
                case 0:
                    return "an unspecified address";
                case 169 when b[1] == 254:
                    return "a link-local address, where the cloud metadata services answer, which no deployment reaches";
                case >= 224:
                    return "a multicast or reserved address";
            }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            {
                return "an unspecified address";
            }

            if (ip.IsIPv6LinkLocal)
            {
                return "a link-local address, which no deployment reaches";
            }

            if (ip.IsIPv6Multicast)
            {
                return "a multicast address";
            }
        }
        else
        {
            return $"an address of the {ip.AddressFamily} family";
        }

        if (PrivateRanges.Any(r => r.Contains(ip)) && !PrivateNetworks.Any(n => n.Contains(ip)))
        {
            return $"a private address, which the nodes reach only when the deployment lists its range under {PrivateNetworksVariable}";
        }

        return null;
    }

    /// <summary>The IPv4 address an IPv6 address carries (mapped, NAT64, 6to4), which is where a connection to it ends up; the address itself otherwise.</summary>
    private static IPAddress Embedded(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (Nat64.Contains(address))
        {
            return new IPAddress(bytes[12..16]);
        }

        return SixToFour.Contains(address) ? new IPAddress(bytes[2..6]) : address;
    }
}
