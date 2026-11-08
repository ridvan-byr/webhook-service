using System.Net;
using WebhookService.Core.Security;

namespace WebhookService.Tests.Security;

public class IpAddressPolicyTests
{
    [Theory]
    // loopback and "this network"
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    // private networks (and their exact edges)
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    // link-local, including the cloud metadata address
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    // carrier-grade NAT, benchmarking, documentation, protocol assignments
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("192.88.99.1")]
    // multicast, reserved, broadcast
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    // IPv6: unspecified, loopback, link-local, unique local, multicast, documentation, Teredo, discard
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")]
    [InlineData("100::1")]
    // IPv6 addresses that only wrap a non-public IPv4 address
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::7f00:1")]       // NAT64 around 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]    // NAT64 around 169.254.169.254
    [InlineData("2002:7f00:1::1")]        // 6to4 around 127.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]     // 6to4 around 169.254.169.254
    [InlineData("::127.0.0.1")]           // IPv4-compatible
    public void Non_public_addresses_are_refused(string text)
    {
        Assert.False(IpAddressPolicy.IsPublic(IPAddress.Parse(text)), $"{text} must be refused");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("11.0.0.1")]
    [InlineData("172.15.255.255")]   // just below 172.16.0.0/12
    [InlineData("172.32.0.1")]       // just above 172.16.0.0/12
    [InlineData("100.63.255.255")]   // just below 100.64.0.0/10
    [InlineData("100.128.0.1")]      // just above 100.64.0.0/10
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2a00:1450:4001:81b::200e")]
    [InlineData("::ffff:8.8.8.8")]    // wrapped public IPv4 stays public
    [InlineData("64:ff9b::808:808")]  // NAT64 around 8.8.8.8
    [InlineData("2002:808:808::1")]   // 6to4 around 8.8.8.8
    public void Public_addresses_are_allowed(string text)
    {
        Assert.True(IpAddressPolicy.IsPublic(IPAddress.Parse(text)), $"{text} must be allowed");
    }

    [Fact]
    public void Every_address_inside_a_blocked_range_is_refused_at_the_range_edges()
    {
        // 172.16.0.0/12 spans 172.16.0.0 to 172.31.255.255. Walk the second octet across the edge.
        for (var second = 0; second <= 40; second++)
        {
            var expectedPublic = second is < 16 or > 31;
            Assert.Equal(expectedPublic, IpAddressPolicy.IsPublic(IPAddress.Parse($"172.{second}.1.1")));
        }
    }

    [Fact]
    public void A_null_address_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => IpAddressPolicy.IsPublic(null!));
    }
}
