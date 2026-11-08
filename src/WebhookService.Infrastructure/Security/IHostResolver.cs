using System.Net;

namespace WebhookService.Infrastructure.Security;

/// <summary>Turns a host name into IP addresses. An interface so tests can simulate DNS answers.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, ct);
}
