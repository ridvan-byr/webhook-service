using System.Net;
using Microsoft.Extensions.Options;

namespace WebhookService.Api.Security;

/// <summary>How many failed attempts to authenticate a client may make, and for how long it is counted.</summary>
public sealed class AuthFailureLimitOptions
{
    public const string SectionName = "Security:AuthFailureLimit";

    /// <summary>Failures allowed inside <see cref="Window"/>. The next request from that client is refused.</summary>
    public int MaxFailures { get; set; } = 20;

    /// <summary>How long a failure counts. A client is let in again when its oldest failure is older than this.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many clients are remembered at most, so a flood of requests from many addresses cannot use up memory.
    /// Past this, an address that is not remembered yet is not counted.
    /// </summary>
    public int MaxTrackedClients { get; set; } = 10_000;

    /// <summary>The limit is a protection, so settings that would switch it off by accident stop the service.</summary>
    public void Validate()
    {
        if (MaxFailures < 1)
        {
            throw new InvalidOperationException($"{SectionName}:MaxFailures must be at least 1.");
        }

        if (Window <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:Window must be longer than zero.");
        }

        if (MaxTrackedClients < 1)
        {
            throw new InvalidOperationException($"{SectionName}:MaxTrackedClients must be at least 1.");
        }
    }
}

/// <summary>
/// Remembers failed attempts to authenticate, per client, and says when a client has made too many.
///
/// The count is a sliding window: a client is blocked while it has <see cref="AuthFailureLimitOptions.MaxFailures"/>
/// failures that are younger than the window. It lives in memory, so it starts again with the process, and each
/// running copy of the API counts for itself.
/// </summary>
public sealed class AuthFailureLimiter
{
    private static readonly TimeSpan MinimumPruneInterval = TimeSpan.FromSeconds(1);

    private readonly AuthFailureLimitOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new();
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public AuthFailureLimiter(IOptions<AuthFailureLimitOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _options.Validate();
        _time = time;
    }

    /// <summary>How many failures are remembered for one client. Never more than the number allowed.</summary>
    internal int TrackedFailures(string client)
    {
        lock (_gate)
        {
            return _failures.TryGetValue(client, out var queue) ? queue.Count : 0;
        }
    }

    /// <summary>How many clients are remembered right now.</summary>
    internal int TrackedClients
    {
        get
        {
            lock (_gate)
            {
                return _failures.Count;
            }
        }
    }

    /// <summary>
    /// Who a request counts as: its address, or for IPv6 its whole /64 network, because one person usually holds
    /// a whole /64 and could otherwise change address at will. An unknown address is one shared client.
    /// </summary>
    public static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>Whether the client has used up its failures, and if so how long until it may try again.</summary>
    public bool IsBlocked(string client, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            if (_failures.TryGetValue(client, out var queue))
            {
                DropExpired(queue, now);
                if (queue.Count == 0)
                {
                    _failures.Remove(client);
                }
                else if (queue.Count >= _options.MaxFailures)
                {
                    // Still blocked means the oldest failure is younger than the window, so there is a wait left.
                    // Rounding it up makes it at least one second: a client is never told to retry "in 0 seconds".
                    var wait = queue.Peek() + _options.Window - now;
                    retryAfter = TimeSpan.FromSeconds(Math.Ceiling(wait.TotalSeconds));
                    return true;
                }
            }

            retryAfter = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>Counts a failure. Returns true for the one that makes the client blocked, which is when to log it.</summary>
    public bool RecordFailure(string client)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            if (!_failures.TryGetValue(client, out var queue))
            {
                if (_failures.Count >= _options.MaxTrackedClients)
                {
                    Prune(now);
                    if (_failures.Count >= _options.MaxTrackedClients)
                    {
                        return false;
                    }
                }

                queue = new Queue<DateTimeOffset>();
                _failures[client] = queue;
            }

            DropExpired(queue, now);
            var before = queue.Count;
            queue.Enqueue(now);

            // Only the newest MaxFailures matter, so the queue never grows past that.
            while (queue.Count > _options.MaxFailures)
            {
                queue.Dequeue();
            }

            return before < _options.MaxFailures && queue.Count >= _options.MaxFailures;
        }
    }

    private void DropExpired(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= _options.Window)
        {
            queue.Dequeue();
        }
    }

    /// <summary>Forgets clients with no failure left in the window. At most once a second, so a flood cannot make it costly.</summary>
    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < MinimumPruneInterval)
        {
            return;
        }

        _lastPrune = now;
        foreach (var (client, queue) in _failures.ToList())
        {
            DropExpired(queue, now);
            if (queue.Count == 0)
            {
                _failures.Remove(client);
            }
        }
    }
}
