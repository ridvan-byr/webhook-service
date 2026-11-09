namespace WebhookService.Infrastructure.Dispatch.RabbitMq;

/// <summary>Sends the wake-up messages. It is a seam so the relay can be tested without a broker.</summary>
public interface IDeliveryPublisher
{
    /// <summary>
    /// Announces the deliveries and waits until the broker has taken responsibility for each message.
    /// </summary>
    /// <returns>The ids the broker did not confirm. Empty means every message was taken.</returns>
    /// <exception cref="Exception">The broker could not be reached at all.</exception>
    Task<IReadOnlyList<Guid>> PublishAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken ct);
}
