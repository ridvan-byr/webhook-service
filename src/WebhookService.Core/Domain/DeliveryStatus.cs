namespace WebhookService.Core.Domain;

public enum DeliveryStatus
{
    /// <summary>Waiting for its next attempt (new, or scheduled for a retry).</summary>
    Pending = 0,

    /// <summary>A worker has claimed it and is currently trying to deliver.</summary>
    InProgress = 1,

    /// <summary>The receiver acknowledged the event.</summary>
    Succeeded = 2,

    /// <summary>All attempts were used up. Needs a manual re-send.</summary>
    Dead = 3,
}
