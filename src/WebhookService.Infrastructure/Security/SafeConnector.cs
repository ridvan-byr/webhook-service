using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookService.Core.Security;

namespace WebhookService.Infrastructure.Security;

/// <summary>Thrown when a destination resolves to an address the service refuses to contact.</summary>
public sealed class BlockedDestinationException : Exception
{
    public BlockedDestinationException()
        : base("The destination is not a public address.")
    {
    }
}

/// <summary>
/// Opens the network connection for every webhook request. This is where the SSRF protection lives.
///
/// It resolves the host name itself, checks EVERY address it got back, and then connects to
/// one of those exact addresses. Because the connection goes to the address that was checked, a
/// host name that answers differently the second time (DNS rebinding) cannot send the request
/// somewhere else than what was verified.
/// </summary>
public sealed class SafeConnector
{
    private readonly IHostResolver _resolver;
    private readonly NetworkSecurityOptions _options;
    private readonly ILogger<SafeConnector> _logger;

    public SafeConnector(IHostResolver resolver, IOptions<NetworkSecurityOptions> options, ILogger<SafeConnector> logger)
    {
        _resolver = resolver;
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await _resolver.ResolveAsync(host, ct);

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (!_options.AllowPrivateNetworks)
        {
            // One bad address is enough to refuse the whole host. Picking only the "good" ones would let
            // an attacker's name mix public and private answers and hope we choose wrong.
            var blocked = addresses.FirstOrDefault(a => !IpAddressPolicy.IsPublic(a));
            if (blocked is not null)
            {
                // The details stay in the server log. The caller only sees a generic message.
                _logger.LogWarning("Blocked a webhook to {Host}: it resolves to non-public address {Address}.", host, blocked);
                throw new BlockedDestinationException();
            }
        }

        SocketException? lastFailure = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastFailure!;
    }
}
