namespace WebhookService.Infrastructure.Dispatch;

/// <summary>
/// A delivery that this worker now owns and has to attempt: the database claim has been made
/// (<see cref="WebhookService.Core.Domain.Delivery.StartAttempt"/>), and <paramref name="AttemptNumber"/> is the
/// attempt it counted.
/// </summary>
public sealed record DeliveryClaim(Guid DeliveryId, int AttemptNumber);

/// <summary>
/// How a worker learns which delivery to attempt next. This is the one thing that differs between ways of
/// carrying the work to a worker; sending, the ownership check, recording the attempt and the state transitions
/// are the same for all of them (<see cref="DeliveryProcessor"/>), and the database remains the source of truth
/// (see ADR 0010).
///
/// A source makes the database claim for each delivery before it calls the handler, and does whatever
/// acknowledgement its transport needs once the handler has returned.
/// </summary>
public interface IDeliverySource
{
    /// <summary>Calls <paramref name="handle"/> for every delivery that should be attempted, until cancelled.</summary>
    /// <remarks>Returns normally when <paramref name="ct"/> is cancelled. A failure of one round does not end it.</remarks>
    Task RunAsync(Func<DeliveryClaim, CancellationToken, Task> handle, CancellationToken ct);
}
