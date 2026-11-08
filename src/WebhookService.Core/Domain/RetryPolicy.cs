namespace WebhookService.Core.Domain;

/// <summary>
/// Decides how many times a delivery is tried and how long to wait between tries.
/// Uses exponential backoff with "equal jitter": half of the delay is fixed, the
/// other half is random. The fixed half guarantees the receiver gets some breathing
/// room; the random half stops many failed deliveries from retrying at the same moment.
/// </summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    public static RetryPolicy Default { get; } =
        new(MaxAttempts: 5, BaseDelay: TimeSpan.FromSeconds(30), MaxDelay: TimeSpan.FromHours(1));

    /// <param name="attemptsMade">How many attempts have already failed (1 after the first failure).</param>
    /// <param name="jitter">A random number in [0, 1]. Injected so tests are deterministic.</param>
    public TimeSpan DelayAfter(int attemptsMade, double jitter)
    {
        if (attemptsMade < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptsMade), "Must be at least 1.");
        }

        if (jitter is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), "Must be between 0 and 1.");
        }

        // BaseDelay * 2^(attemptsMade - 1), capped. Cap the exponent first so it cannot overflow.
        var exponent = Math.Min(attemptsMade - 1, 30);
        var uncappedTicks = BaseDelay.Ticks * Math.Pow(2, exponent);
        var cappedTicks = Math.Min(uncappedTicks, MaxDelay.Ticks);

        var half = cappedTicks / 2;
        return TimeSpan.FromTicks((long)(half + half * jitter));
    }
}
