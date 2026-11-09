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
        var failed = new List<Guid>();
        var published = 0;
        Exception? firstError = null;

        foreach (var id in deliveryIds)
        {
            if (firstError is not null && firstError is not PublishException)
            {
                // The channel or connection is gone: asking again for each remaining message would only wait.
                failed.Add(id);
                continue;
            }

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
                published++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Includes our own timeout: a broker that does not answer is a broker that did not confirm.
                firstError ??= ex;
                failed.Add(id);
                _logger.LogWarning(ex, "Delivery {DeliveryId} was not confirmed by the broker.", id);
            }
        }

        if (published == 0 && firstError is not null)
        {
            // Nothing got through: tell the relay the broker could not be used, rather than listing every id.
            throw firstError;
        }

        return failed;
    }
}
