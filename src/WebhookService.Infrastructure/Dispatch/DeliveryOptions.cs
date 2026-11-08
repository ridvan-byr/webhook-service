using WebhookService.Core.Domain;

namespace WebhookService.Infrastructure.Dispatch;

/// <summary>Settings of the delivery worker, read from the "Delivery" configuration section.</summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>How many due deliveries one worker claims at a time.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>How many HTTP requests one worker sends at the same time.</summary>
    public int MaxParallelism { get; set; } = 8;

    /// <summary>
    /// How long a claim is valid. If a worker dies, its deliveries become available to others
    /// after this time. Must be longer than <see cref="RequestTimeout"/>.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the worker sleeps when there is nothing to do.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int MaxAttempts { get; set; } = RetryPolicy.Default.MaxAttempts;

    public TimeSpan BaseDelay { get; set; } = RetryPolicy.Default.BaseDelay;

    public TimeSpan MaxDelay { get; set; } = RetryPolicy.Default.MaxDelay;

    public RetryPolicy ToRetryPolicy() => new(MaxAttempts, BaseDelay, MaxDelay);

    public void EnsureValid()
    {
        if (BatchSize < 1)
        {
            throw new InvalidOperationException("Delivery:BatchSize must be at least 1.");
        }

        if (MaxParallelism < 1)
        {
            throw new InvalidOperationException("Delivery:MaxParallelism must be at least 1.");
        }

        if (MaxAttempts < 1)
        {
            throw new InvalidOperationException("Delivery:MaxAttempts must be at least 1.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Delivery:RequestTimeout must be positive.");
        }

        // If the lock were shorter than the request, a slow request would be picked up a second
        // time by another worker while the first one is still running.
        if (LockDuration <= RequestTimeout)
        {
            throw new InvalidOperationException("Delivery:LockDuration must be longer than Delivery:RequestTimeout.");
        }
    }
}
