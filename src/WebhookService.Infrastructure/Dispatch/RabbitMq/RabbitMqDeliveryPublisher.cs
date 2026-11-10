using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace WebhookService.Infrastructure.Dispatch.RabbitMq;

/// <summary>
/// Publishes wake-up messages with publisher confirms: a message counts as announced only once the broker has
/// durably taken it (it is persistent, and the queue is durable). A message the broker cannot route, or refuses, is
/// reported back to the relay, which gives that delivery's lease back.
/// </summary>
public sealed class RabbitMqDeliveryPublisher : IDeliveryPublisher
{
    private readonly RabbitMqConnection _connection;
    private readonly ILogger<RabbitMqDeliveryPublisher> _logger;

    public RabbitMqDeliveryPublisher(RabbitMqConnection connection, ILogger<RabbitMqDeliveryPublisher> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Guid>> PublishAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken ct)
    {
        var options = _connection.Options;
        var connection = await _connection.GetAsync(ct);

        // With confirmation tracking on, each publish completes only when the broker has confirmed that message.
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

        try
        {
            return await PublishAllAsync(channel, options, deliveryIds, ct);
        }
        finally
        {
            // A blocked broker can make closing wait as well, and the relay must not hang on it.
            try
            {
                await channel.DisposeAsync().AsTask().WaitAsync(options.PublishTimeout);
            }
            catch
            {
                // Nothing more can be done for a channel that will not close.
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> PublishAllAsync(
        IChannel channel,
        RabbitMqOptions options,
        IReadOnlyList<Guid> deliveryIds,
        CancellationToken ct)
    {
        // All of them are sent at once and then awaited together. Waiting for each confirm before sending the next
        // costs one round trip to the broker (and, for a durable quorum queue, one write to disk) per message; sent
        // together, the broker confirms them in groups. The first version did it one by one and delivered about a
        // quarter as many deliveries per second as the database transport (see docs/performance.md).
        var attempts = deliveryIds.Select(id => PublishOneAsync(channel, options, id, ct)).ToList();
        var errors = await Task.WhenAll(attempts);

        var failed = new List<Guid>();
        Exception? firstError = null;
        for (var i = 0; i < errors.Length; i++)
        {
            if (errors[i] is { } error)
            {
                firstError ??= error;
                failed.Add(deliveryIds[i]);
                _logger.LogWarning(error, "Delivery {DeliveryId} was not confirmed by the broker.", deliveryIds[i]);
            }
        }

        if (failed.Count == deliveryIds.Count && firstError is not null)
        {
            // Nothing got through: tell the relay the broker could not be used, rather than listing every id.
            throw firstError;
        }

        return failed;
    }

    /// <returns>Null when the broker confirmed the message, otherwise why it did not.</returns>
    private static async Task<Exception?> PublishOneAsync(IChannel channel, RabbitMqOptions options, Guid id, CancellationToken ct)
    {
        try
        {
            // The time limit is enforced here and not only handed to the client: a broker that has blocked its
            // publishers (for example because it is out of memory) does not react to a cancelled token, and the
            // relay would wait for ever.
            await channel.BasicPublishAsync(
                exchange: options.Exchange,
                routingKey: options.Queue,
                mandatory: true,
                basicProperties: new BasicProperties { Persistent = true, ContentType = "application/json" },
                body: DeliveryMessage.Create(id),
                cancellationToken: ct).AsTask().WaitAsync(options.PublishTimeout, ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes our own timeout: a broker that does not answer is a broker that did not confirm.
            return ex;
        }
    }
}
