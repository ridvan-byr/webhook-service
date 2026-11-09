using WebhookService.Core.Domain;

namespace WebhookService.Tests.Domain;

/// <summary>
/// The "enqueue lease": a mark on a due delivery saying that a message announcing it has been sent, so it is left
/// alone until the lease runs out. If the message is lost, or nobody acts on it, the lease expires and the delivery
/// is announced again. The mark never changes what the delivery is: its status, attempts and due time stay as they were.
/// </summary>
public class DeliveryEnqueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Lock = TimeSpan.FromMinutes(2);
    private static readonly RetryPolicy Policy = new(MaxAttempts: 2, BaseDelay: TimeSpan.FromSeconds(10), MaxDelay: TimeSpan.FromMinutes(5));

    private static Delivery NewDelivery() => Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), T0);

    private static Delivery Abandoned()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock); // claimed, and the worker never reported back
        return delivery;
    }

    [Fact]
    public void A_new_delivery_has_not_been_announced()
    {
        Assert.Null(NewDelivery().EnqueuedUntil);
    }

    [Fact]
    public void A_due_delivery_is_marked_until_the_lease_ends()
    {
        var delivery = NewDelivery();

        delivery.MarkEnqueued(T0, Lease);

        Assert.Equal(T0 + Lease, delivery.EnqueuedUntil);
    }

    [Fact]
    public void Marking_does_not_change_what_the_delivery_is()
    {
        var delivery = NewDelivery();

        delivery.MarkEnqueued(T0, Lease);

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Equal(T0, delivery.NextAttemptAt);
        Assert.Null(delivery.LockedUntil);
    }

    [Fact]
    public void A_delivery_abandoned_by_a_crashed_worker_can_be_marked_once_its_lock_has_expired()
    {
        var delivery = Abandoned();

        delivery.MarkEnqueued(T0 + Lock, Lease);

        Assert.Equal(T0 + Lock + Lease, delivery.EnqueuedUntil);
        Assert.Equal(DeliveryStatus.InProgress, delivery.Status);
    }

    [Fact]
    public void A_delivery_that_is_not_due_yet_cannot_be_marked()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);
        delivery.MarkFailed(T0, "boom", Policy, jitter: 1); // a retry in the future

        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.MarkEnqueued(T0, Lease));
    }

    [Fact]
    public void A_delivery_being_attempted_with_a_valid_lock_cannot_be_marked()
    {
        var delivery = Abandoned();

        // A slow but healthy attempt is not announced a second time, whatever the lease is.
        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.MarkEnqueued(T0 + Lock - TimeSpan.FromSeconds(1), Lease));
    }

    [Fact]
    public void A_delivery_that_is_finished_cannot_be_marked()
    {
        var succeeded = NewDelivery();
        succeeded.StartAttempt(T0, Lock);
        succeeded.MarkSucceeded();

        var dead = NewDelivery();
        dead.StartAttempt(T0, Lock);
        dead.MarkFailed(T0, "boom", Policy, 1);
        dead.StartAttempt(T0 + TimeSpan.FromMinutes(10), Lock);
        dead.MarkFailed(T0 + TimeSpan.FromMinutes(10), "boom", Policy, 1);

        Assert.Equal(DeliveryStatus.Dead, dead.Status);
        Assert.Throws<InvalidDeliveryTransitionException>(() => succeeded.MarkEnqueued(T0, Lease));
        Assert.Throws<InvalidDeliveryTransitionException>(() => dead.MarkEnqueued(T0 + TimeSpan.FromMinutes(11), Lease));
    }

    [Fact]
    public void A_delivery_cannot_be_marked_again_while_its_lease_lasts()
    {
        var delivery = NewDelivery();
        delivery.MarkEnqueued(T0, Lease);

        Assert.Throws<InvalidDeliveryTransitionException>(() => delivery.MarkEnqueued(T0 + Lease - TimeSpan.FromSeconds(1), Lease));
    }

    [Fact]
    public void It_can_be_marked_again_when_the_lease_has_run_out_because_the_message_may_have_been_lost()
    {
        var delivery = NewDelivery();
        delivery.MarkEnqueued(T0, Lease);

        delivery.MarkEnqueued(T0 + Lease, Lease); // exactly when the first lease ends

        Assert.Equal(T0 + Lease + Lease, delivery.EnqueuedUntil);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_lease_that_is_not_positive_is_refused(int seconds)
    {
        var delivery = NewDelivery();

        Assert.Throws<ArgumentOutOfRangeException>(() => delivery.MarkEnqueued(T0, TimeSpan.FromSeconds(seconds)));
        Assert.Null(delivery.EnqueuedUntil);
    }

    [Fact]
    public void Whether_a_delivery_is_leased_is_a_question_of_the_time_it_is_asked_at()
    {
        var delivery = NewDelivery();
        Assert.False(delivery.IsEnqueuedAt(T0));

        delivery.MarkEnqueued(T0, Lease);

        Assert.True(delivery.IsEnqueuedAt(T0));
        Assert.True(delivery.IsEnqueuedAt(T0 + Lease - TimeSpan.FromTicks(1)));
        Assert.False(delivery.IsEnqueuedAt(T0 + Lease));
    }

    // ---------- the lease ends when it has done its job ----------

    [Fact]
    public void A_worker_can_still_claim_a_marked_delivery_and_the_claim_ends_the_lease()
    {
        // A delivery announced by message can also be taken by a worker that polls the database.
        var delivery = NewDelivery();
        delivery.MarkEnqueued(T0, Lease);

        delivery.StartAttempt(T0, Lock);

        Assert.Equal(DeliveryStatus.InProgress, delivery.Status);
        Assert.Null(delivery.EnqueuedUntil);
    }

    [Fact]
    public void Success_ends_the_lease()
    {
        var delivery = NewDelivery();
        delivery.MarkEnqueued(T0, Lease);
        delivery.StartAttempt(T0, Lock);
        delivery.MarkSucceeded();

        Assert.Null(delivery.EnqueuedUntil);
    }

    [Fact]
    public void A_failed_attempt_ends_the_lease_so_the_retry_is_announced_when_it_is_due_and_not_when_the_old_lease_ran_out()
    {
        var delivery = NewDelivery();
        delivery.MarkEnqueued(T0, TimeSpan.FromHours(1)); // far longer than the retry delay
        delivery.StartAttempt(T0, Lock);

        delivery.MarkFailed(T0, "boom", Policy, jitter: 1);

        Assert.Null(delivery.EnqueuedUntil);
        var due = delivery.NextAttemptAt;
        delivery.MarkEnqueued(due, Lease); // announced at its own time
        Assert.Equal(due + Lease, delivery.EnqueuedUntil);
    }

    [Fact]
    public void A_slow_worker_that_finishes_after_its_lock_ran_out_and_a_lease_was_put_on_the_delivery_ends_that_lease_by_succeeding()
    {
        // The lock expired, the relay took the delivery for abandoned and leased it, and then the original worker
        // finished after all. Its result is accepted, and the lease no longer means anything.
        var delivery = Abandoned();
        delivery.MarkEnqueued(T0 + Lock, Lease);
        Assert.NotNull(delivery.EnqueuedUntil);

        delivery.MarkSucceeded();

        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Null(delivery.EnqueuedUntil);
    }

    [Fact]
    public void A_slow_worker_that_finishes_after_its_lock_ran_out_and_a_lease_was_put_on_the_delivery_ends_that_lease_by_failing()
    {
        var delivery = Abandoned();
        delivery.MarkEnqueued(T0 + Lock, TimeSpan.FromHours(1));

        delivery.MarkFailed(T0 + Lock, "boom", Policy, jitter: 1);

        Assert.Null(delivery.EnqueuedUntil);
        // The retry is announced when it is due, and not an hour later when the old lease would have run out.
        var due = delivery.NextAttemptAt;
        delivery.MarkEnqueued(due, Lease);
        Assert.Equal(due + Lease, delivery.EnqueuedUntil);
    }

    [Fact]
    public void Giving_up_ends_the_lease()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);
        delivery.MarkFailed(T0, "boom", Policy, 1);
        var retryAt = delivery.NextAttemptAt;
        delivery.MarkEnqueued(retryAt, Lease);
        delivery.StartAttempt(retryAt, Lock);

        delivery.MarkFailed(retryAt, "boom", Policy, 1);

        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        Assert.Null(delivery.EnqueuedUntil);
    }

    [Fact]
    public void A_manual_re_send_starts_without_a_lease()
    {
        var delivery = NewDelivery();
        delivery.StartAttempt(T0, Lock);
        delivery.MarkFailed(T0, "boom", Policy, 1);
        delivery.StartAttempt(delivery.NextAttemptAt, Lock);
        delivery.MarkFailed(delivery.NextAttemptAt, "boom", Policy, 1);

        delivery.Requeue(T0 + TimeSpan.FromHours(1));

        Assert.Null(delivery.EnqueuedUntil);
    }

    // ---------- giving the lease back ----------

    [Fact]
    public void A_lease_can_be_given_back_when_the_message_could_not_be_sent_and_the_delivery_is_then_due_at_once()
    {
        var delivery = NewDelivery();
        delivery.MarkEnqueued(T0, Lease);

        delivery.ReleaseEnqueue();

        Assert.Null(delivery.EnqueuedUntil);
        delivery.MarkEnqueued(T0, Lease); // not refused: nothing is left of the first lease
        Assert.Equal(T0 + Lease, delivery.EnqueuedUntil);
    }

    [Fact]
    public void Giving_back_a_lease_that_was_never_taken_changes_nothing()
    {
        var delivery = NewDelivery();

        delivery.ReleaseEnqueue();

        Assert.Null(delivery.EnqueuedUntil);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
    }
}
