using WebhookService.Core.Domain;

namespace WebhookService.Tests.Domain;

public class DeliveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lock = TimeSpan.FromMinutes(2);
    private static readonly RetryPolicy Policy = new(
        MaxAttempts: 3,
        BaseDelay: TimeSpan.FromSeconds(10),
        MaxDelay: TimeSpan.FromMinutes(5));

    private static Delivery NewDelivery() => Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), T0);

    [Fact]
    public void New_delivery_is_pending_and_due_immediately()
    {
        var delivery = NewDelivery();

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Equal(T0, delivery.NextAttemptAt);
    }

    [Fact]
    public void StartAttempt_claims_the_delivery_and_counts_the_attempt()
    {
        var delivery = NewDelivery();

        delivery.StartAttempt(T0, Lock);

        Assert.Equal(DeliveryStatus.InProgress, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(T0 + Lock, delivery.LockedUntil);
    }

    [Fact]
    public void StartAttempt_before_it_is_due_is_rejected()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);
        delivery.MarkFailed(T0, "boom", Policy, jitter: 1);

        // Retry is scheduled in the future, so claiming it now must fail.
        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.StartAttempt(T0, Lock));
    }

    [Fact]
    public void A_claimed_delivery_cannot_be_claimed_again_while_the_lock_is_valid()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);

        Assert.Throws<InvalidDeliveryTransitionException>(
            () => delivery.StartAttempt(T0 + TimeSpan.FromSeconds(30), Lock));
    }

    [Fact]
    public void A_delivery_abandoned_by_a_crashed_worker_can_be_claimed_after_the_lock_expires()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);

        // The worker never reported back. Once the lock is over, another worker takes over.
        var later = T0 + Lock + TimeSpan.FromSeconds(1);
        delivery.StartAttempt(later, Lock);

        Assert.Equal(DeliveryStatus.InProgress, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
    }

    [Fact]
    public void MarkSucceeded_completes_the_delivery_and_clears_the_lock()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);

        delivery.MarkSucceeded();

        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Null(delivery.LockedUntil);
    }

    [Fact]
    public void A_failed_attempt_schedules_a_retry_with_backoff()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);

        delivery.MarkFailed(T0, "HTTP 500", Policy, jitter: 1);

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal("HTTP 500", delivery.LastError);
        Assert.Equal(T0 + TimeSpan.FromSeconds(10), delivery.NextAttemptAt);
        Assert.Null(delivery.LockedUntil);
    }

    [Fact]
    public void The_delivery_dies_when_all_attempts_are_used()
    {
        var delivery = NewDelivery();
        var now = T0;

        for (var attempt = 1; attempt <= Policy.MaxAttempts; attempt++)
        {
            delivery.StartAttempt(now, Lock);
            delivery.MarkFailed(now, "down", Policy, jitter: 1);
            now = delivery.NextAttemptAt;
        }

        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        Assert.Equal(Policy.MaxAttempts, delivery.AttemptCount);
    }

    [Fact]
    public void A_dead_delivery_is_never_claimed_by_a_worker()
    {
        var delivery = MakeDead();

        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.StartAttempt(T0.AddYears(1), Lock));
    }

    [Fact]
    public void Requeue_gives_a_dead_delivery_a_fresh_set_of_attempts()
    {
        var delivery = MakeDead();
        var now = T0.AddDays(1);

        delivery.Requeue(now);

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Equal(now, delivery.NextAttemptAt);
    }

    [Fact]
    public void Requeue_is_only_allowed_for_dead_deliveries()
    {
        var delivery = NewDelivery();

        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.Requeue(T0));
    }

    [Fact]
    public void Results_can_only_be_reported_for_a_delivery_that_is_in_progress()
    {
        var delivery = NewDelivery();

        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.MarkSucceeded());
        Assert.Throws<InvalidDeliveryTransitionException>(
            () => delivery.MarkFailed(T0, "x", Policy, jitter: 0));
    }

    [Fact]
    public void A_succeeded_delivery_cannot_change_again()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);
        delivery.MarkSucceeded();

        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.MarkSucceeded());
        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.StartAttempt(T0.AddDays(1), Lock));
        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.Requeue(T0));
    }

    private static Delivery MakeDead()
    {
        var delivery = NewDelivery();
        var now = T0;

        for (var attempt = 1; attempt <= Policy.MaxAttempts; attempt++)
        {
            delivery.StartAttempt(now, Lock);
            delivery.MarkFailed(now, "down", Policy, jitter: 0);
            now = delivery.NextAttemptAt;
        }

        return delivery;
    }
}
