using WebhookService.Infrastructure.Dispatch;

namespace WebhookService.Tests.Dispatch;

/// <summary>A clock the test controls, so "an hour later" does not mean waiting an hour.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private int _reads;

    /// <summary>How many times the time was asked for. A worker asks once for every round of claiming.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Increment(ref _reads);
        return _now;
    }

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Always the same "random" number, so retry times can be asserted exactly.</summary>
public sealed class FixedJitter : IJitter
{
    public double Next() => 1.0;
}
