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
    }

    public void MarkSucceeded()
    {
        EnsureInProgress("mark as succeeded");

        Status = DeliveryStatus.Succeeded;
        LockedUntil = null;
        LastError = null;
    }

    /// <summary>Records a failed attempt: schedule a retry, or give up and mark the delivery Dead.</summary>
    public void MarkFailed(DateTimeOffset now, string error, RetryPolicy policy, double jitter)
    {
        EnsureInProgress("mark as failed");

        LastError = error;
        LockedUntil = null;

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
    }

    private void EnsureInProgress(string action)
    {
        if (Status != DeliveryStatus.InProgress)
        {
            throw new InvalidDeliveryTransitionException(Status, action);
        }
    }
}
