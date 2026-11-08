using System.Collections.Concurrent;
using System.Net;
using WebhookService.Infrastructure.Security;

namespace WebhookService.Tests.Dispatch;

/// <summary>Plays the part of DNS: each host name gets the addresses the test decides.</summary>
public sealed class FakeResolver : IHostResolver
{
    private readonly Dictionary<string, IPAddress[]> _answers;

    public FakeResolver(Dictionary<string, string[]> answers) =>
        _answers = answers.ToDictionary(a => a.Key, a => a.Value.Select(IPAddress.Parse).ToArray());

    public ConcurrentQueue<string> Lookups { get; } = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Lookups.Enqueue(host);

        return _answers.TryGetValue(host, out var addresses)
            ? Task.FromResult(addresses)
            : throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
    }
}
