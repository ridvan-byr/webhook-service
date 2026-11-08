using System.Net;
using System.Text.RegularExpressions;

namespace WebhookService.Core.Security;

/// <summary>
/// Checks a webhook URL when an endpoint is registered. It gives an early, readable error for
/// obviously wrong URLs. It is NOT the real protection: a host name can point anywhere later.
/// The real protection is the check at connection time (SafeConnector in the infrastructure).
/// </summary>
public static partial class EndpointUrlValidator
{
    private static readonly string[] BlockedSuffixes = [".localhost", ".local", ".internal", ".localdomain", ".lan", ".home.arpa"];

    // Host names made only of numbers, such as "2130706433" or "0x7f.1", are read as IP addresses by some
    // systems and as names by others. Refuse the ambiguity instead of guessing.
    [GeneratedRegex(@"^(0[xX][0-9a-fA-F]+|[0-9]+)(\.(0[xX][0-9a-fA-F]+|[0-9]+))*$")]
    private static partial Regex NumericHost();

    /// <returns>The parsed URL, or an error message that is safe to show to the caller.</returns>
    public static (Uri? Url, string? Error) Validate(string? text, bool requireHttps, bool allowPrivateNetworks)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            return (null, "Must be an absolute URL.");
        }

        if (url.Scheme is not ("http" or "https"))
        {
            return (null, "Only http and https URLs are supported.");
        }

        if (requireHttps && url.Scheme != "https")
        {
            return (null, "Must use https.");
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return (null, "Must not contain a user name or password.");
        }

        if (!allowPrivateNetworks)
        {
            var hostError = CheckHost(url);
            if (hostError is not null)
            {
                return (null, hostError);
            }
        }

        return (url, null);
    }

    private static string? CheckHost(Uri url)
    {
        const string notPublic = "Must not point to a private, local or reserved address.";

        // Uri has already turned shortcuts like "2130706433" or "127.1" into a real IP address here.
        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            var literal = url.Host.Trim('[', ']');
            return IPAddress.TryParse(literal, out var ip) && IpAddressPolicy.IsPublic(ip) ? null : notPublic;
        }

        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();

        if (host.Length == 0 || NumericHost().IsMatch(host))
        {
            return "Must be a normal host name or a public IP address.";
        }

        // A name without a dot ("intranet", "localhost") only makes sense inside a private network.
        if (!host.Contains('.') || BlockedSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return notPublic;
        }

        return null;
    }
}
