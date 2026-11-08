using WebhookService.Core.Security;

namespace WebhookService.Tests.Security;

public class EndpointUrlValidatorTests
{
    private static string? Error(string? url, bool requireHttps = true, bool allowPrivate = false) =>
        EndpointUrlValidator.Validate(url, requireHttps, allowPrivate).Error;

    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("https://hooks.example.co.uk:8443/path?x=1")]
    [InlineData("https://8.8.8.8/hook")]
    [InlineData("https://[2606:4700:4700::1111]/hook")]
    [InlineData("https://xn--bcher-kva.example/hook")]
    public void Normal_public_https_urls_are_accepted(string url)
    {
        Assert.Null(Error(url));
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("https://192.168.0.10/hook")]
    [InlineData("https://172.20.0.5/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://100.100.100.200/")]
    [InlineData("https://0.0.0.0/")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fe80::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]
    [InlineData("https://[::ffff:127.0.0.1]/hook")]
    [InlineData("https://[::ffff:169.254.169.254]/hook")]
    public void Private_and_reserved_addresses_are_refused(string url)
    {
        Assert.NotNull(Error(url));
    }

    [Theory]
    [InlineData("https://2130706433/")]          // 127.0.0.1 as one decimal number
    [InlineData("https://0x7f000001/")]          // hexadecimal
    [InlineData("https://0x7f.0.0.1/")]          // hexadecimal part
    [InlineData("https://017700000001/")]        // octal
    [InlineData("https://127.1/")]               // short form
    [InlineData("https://127.0.1/")]             // short form
    [InlineData("https://2852039166/")]          // 169.254.169.254 as one decimal number
    public void Disguised_numeric_addresses_are_refused(string url)
    {
        Assert.NotNull(Error(url));
    }

    [Theory]
    [InlineData("https://localhost/hook")]
    [InlineData("https://LOCALHOST/hook")]
    [InlineData("https://localhost./hook")]
    [InlineData("https://app.localhost/hook")]
    [InlineData("https://printer.local/hook")]
    [InlineData("https://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("https://db.internal/")]
    [InlineData("https://intranet/hook")]
    [InlineData("https://router.lan/")]
    [InlineData("https://nas.home.arpa/")]
    [InlineData("https://host.localdomain/")]
    public void Names_that_only_exist_inside_a_private_network_are_refused(string url)
    {
        Assert.NotNull(Error(url));
    }

    [Theory]
    [InlineData("https://user:password@example.com/hook")]
    [InlineData("https://user@example.com/hook")]
    public void Credentials_inside_the_url_are_refused(string url)
    {
        Assert.NotNull(Error(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("example.com/hook")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    public void Things_that_are_not_http_or_https_urls_are_refused(string? url)
    {
        Assert.NotNull(Error(url));
    }

    [Fact]
    public void Plain_http_is_refused_when_https_is_required_and_accepted_otherwise()
    {
        Assert.NotNull(Error("http://example.com/hook", requireHttps: true));
        Assert.Null(Error("http://example.com/hook", requireHttps: false));
    }

    [Fact]
    public void Private_addresses_are_accepted_only_when_explicitly_allowed()
    {
        Assert.NotNull(Error("https://127.0.0.1/hook", allowPrivate: false));
        Assert.Null(Error("https://127.0.0.1/hook", allowPrivate: true));
        Assert.Null(Error("https://localhost/hook", allowPrivate: true));
    }

    [Fact]
    public void Allowing_private_networks_does_not_allow_credentials_or_odd_schemes()
    {
        Assert.NotNull(Error("https://user:pw@localhost/", allowPrivate: true));
        Assert.NotNull(Error("ftp://localhost/", allowPrivate: true));
    }

    [Fact]
    public void The_error_message_never_repeats_the_address()
    {
        var error = Error("https://169.254.169.254/latest/meta-data/");

        Assert.DoesNotContain("169.254", error);
    }
}
