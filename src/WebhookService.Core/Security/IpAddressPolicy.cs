using System.Net;
using System.Net.Sockets;

namespace WebhookService.Core.Security;

/// <summary>
/// Decides whether an IP address is a normal public internet address. Anything else (loopback,
/// private networks, link-local such as the cloud metadata address 169.254.169.254, reserved
/// ranges, ...) is refused as a webhook destination, so the service cannot be used to reach the
/// network it runs in.
///
/// This is a deny list of everything that is not meant for the public internet. IPv6 addresses
/// that merely wrap an IPv4 address (IPv4-mapped, NAT64, 6to4) are judged by the IPv4 address
/// they contain, otherwise "::ffff:127.0.0.1" would slip through.
/// </summary>
public static class IpAddressPolicy
{
    // IPv4 ranges that are not public. (network, prefix length)
    private static readonly (string Network, int Prefix)[] BlockedIPv4 =
    [
        ("0.0.0.0", 8),         // "this network"
        ("10.0.0.0", 8),        // private
        ("100.64.0.0", 10),     // carrier-grade NAT
        ("127.0.0.0", 8),       // loopback
        ("169.254.0.0", 16),    // link-local, includes the cloud metadata address
        ("172.16.0.0", 12),     // private
        ("192.0.0.0", 24),      // IETF protocol assignments
        ("192.0.2.0", 24),      // documentation
        ("192.88.99.0", 24),    // old 6to4 relay
        ("192.168.0.0", 16),    // private
        ("198.18.0.0", 15),     // benchmarking
        ("198.51.100.0", 24),   // documentation
        ("203.0.113.0", 24),    // documentation
        ("224.0.0.0", 4),       // multicast
        ("240.0.0.0", 4),       // reserved, includes the broadcast address
    ];

    // IPv6 ranges that are not public.
    private static readonly (string Network, int Prefix)[] BlockedIPv6 =
    [
        ("::", 128),            // unspecified
        ("::1", 128),           // loopback
        ("100::", 64),          // discard-only
        ("2001::", 32),         // Teredo
        ("2001:db8::", 32),     // documentation
        ("fc00::", 7),          // unique local (private)
        ("fe80::", 10),         // link-local
        ("ff00::", 8),          // multicast
    ];

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !InAny(address, BlockedIPv4);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        // An IPv6 address that only carries an IPv4 address is judged by the IPv4 address.
        if (TryGetEmbeddedIPv4(address, out var embedded))
        {
            return IsPublic(embedded);
        }

        return !InAny(address, BlockedIPv6);
    }

    private static bool TryGetEmbeddedIPv4(IPAddress address, out IPAddress embedded)
    {
        // ::ffff:a.b.c.d (IPv4-mapped)
        if (address.IsIPv4MappedToIPv6)
        {
            embedded = address.MapToIPv4();
            return true;
        }

        var bytes = address.GetAddressBytes();

        // 64:ff9b::/96 (NAT64): the IPv4 address is in the last four bytes.
        if (HasPrefix(bytes, IPAddress.Parse("64:ff9b::").GetAddressBytes(), 96))
        {
            embedded = new IPAddress(bytes[12..16]);
            return true;
        }

        // 2002::/16 (6to4): the IPv4 address follows the 16-bit prefix.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            embedded = new IPAddress(bytes[2..6]);
            return true;
        }

        // ::a.b.c.d (IPv4-compatible, deprecated): first 96 bits are zero. "::" and "::1" are
        // handled by the deny list, so only treat clearly embedded addresses here.
        if (HasPrefix(bytes, new byte[16], 96) && (bytes[12] | bytes[13] | bytes[14]) != 0)
        {
            embedded = new IPAddress(bytes[12..16]);
            return true;
        }

        embedded = IPAddress.None;
        return false;
    }

    private static bool InAny(IPAddress address, (string Network, int Prefix)[] ranges)
    {
        var bytes = address.GetAddressBytes();

        foreach (var (network, prefix) in ranges)
        {
            if (HasPrefix(bytes, IPAddress.Parse(network).GetAddressBytes(), prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the first <paramref name="prefixBits"/> bits of both addresses are equal.</summary>
    private static bool HasPrefix(byte[] address, byte[] network, int prefixBits)
    {
        var fullBytes = prefixBits / 8;
        var remainingBits = prefixBits % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (address[i] != network[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (address[fullBytes] & mask) == (network[fullBytes] & mask);
    }
}
