namespace WebhookService.Core.Domain;

/// <summary>
/// One event that has to reach one endpoint. This is the unit the worker processes.
/// All state changes go through methods so illegal transitions are impossible.
///
/// Pending -> InProgress -> Succeeded
///               |-> Pending (failed, will retry)
///               |-> Dead    (attempts used up) -> Pending (manual re-send)
/// </summary>
public sealed class Delivery
{
    // Parameterless constructor is for the persistence layer (added later).
    private Delivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid EndpointId { get; private set; }

    public DeliveryStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>While InProgress: the claim expires at this time (protects against crashed workers).</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// When set, a message announcing this delivery has been sent and it is left alone until this time. If the
    /// message was lost or nobody acted on it, the lease runs out and the delivery is announced again.
    /// It does not change what the delivery is: the status, the attempts and the due time stay as they were,
    /// and a worker that polls the database can still claim it.
    /// </summary>
    public DateTimeOffset? EnqueuedUntil { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Delivery Create(Guid eventId, Guid endpointId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        EventId = eventId,
        EndpointId = endpointId,
        Status = DeliveryStatus.Pending,
        AttemptCount = 0,
        NextAttemptAt = now,
        CreatedAt = now,
    };

    /// <summary>
    /// A worker claims the delivery. Allowed when it is due (Pending), or when a previous
    /// worker claimed it and never reported back (InProgress with an expired lock).
    /// </summary>
    public void StartAttempt(DateTimeOffset now, TimeSpan lockDuration)
    {
        var isDue = Status == DeliveryStatus.Pending && NextAttemptAt <= now;
        var isAbandoned = Status == DeliveryStatus.InProgress && LockedUntil <= now;

        if (!isDue && !isAbandoned)
        {
            throw new InvalidDeliveryTransitionException(Status, "start an attempt on");
        }

        Status = DeliveryStatus.InProgress;
        AttemptCount++;
        LockedUntil = now + lockDuration;

        // The message (if there was one) did its job. If this worker dies, the delivery is picked up when the
        // lock expires, not when a lease that is no longer relevant would.
        EnqueuedUntil = null;
    }

    /// <summary>
    /// Whether a message announcing this delivery was sent and its lease has not run out at <paramref name="now"/>.
    /// </summary>
    public bool IsEnqueuedAt(DateTimeOffset now) => EnqueuedUntil > now;

    /// <summary>
    /// Records that a message announcing this delivery is being sent, so it is not announced again for
    /// <paramref name="lease"/>. Allowed for a delivery a worker could claim right now (due, or abandoned by a
    /// worker whose lock expired) that is not already under a lease.
    /// </summary>
    public void MarkEnqueued(DateTimeOffset now, TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), lease, "The lease must be longer than zero.");
        }

        var isDue = Status == DeliveryStatus.Pending && NextAttemptAt <= now;
        var isAbandoned = Status == DeliveryStatus.InProgress && LockedUntil <= now;

        if (!isDue && !isAbandoned)
        {
            throw new InvalidDeliveryTransitionException(Status, "announce");
        }

        if (IsEnqueuedAt(now))
        {
            throw new InvalidDeliveryTransitionException(Status, "announce again while its lease lasts, on");
        }

        EnqueuedUntil = now + lease;
    }

    /// <summary>The message could not be sent. Gives the lease back, so the delivery can be announced again at once.</summary>
    public void ReleaseEnqueue() => EnqueuedUntil = null;

    public void MarkSucceeded()
    {
        EnsureInProgress("mark as succeeded");

        Status = DeliveryStatus.Succeeded;
        LockedUntil = null;
        EnqueuedUntil = null;
        LastError = null;
    }

    /// <summary>Records a failed attempt: schedule a retry, or give up and mark the delivery Dead.</summary>
    public void MarkFailed(DateTimeOffset now, string error, RetryPolicy policy, double jitter)
    {
        EnsureInProgress("mark as failed");

        LastError = error;
        LockedUntil = null;

        // A retry is announced when it is due, not when an older lease happens to run out.
        EnqueuedUntil = null;

        if (AttemptCount >= policy.MaxAttempts)
        {
            Status = DeliveryStatus.Dead;
            return;
        }

        Status = DeliveryStatus.Pending;
        NextAttemptAt = now + policy.DelayAfter(AttemptCount, jitter);
    }

    /// <summary>Manual re-send of a dead delivery. It gets a fresh set of attempts.</summary>
    public void Requeue(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Dead)
        {
            throw new InvalidDeliveryTransitionException(Status, "re-send");
        }

        Status = DeliveryStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now;
        EnqueuedUntil = null;
    }

    private void EnsureInProgress(string action)
    {
        if (Status != DeliveryStatus.InProgress)
        {
            throw new InvalidDeliveryTransitionException(Status, action);
        }
    }
}
