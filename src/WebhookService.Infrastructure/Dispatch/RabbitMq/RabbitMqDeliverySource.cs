using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace WebhookService.Infrastructure.Dispatch.RabbitMq;

/// <summary>What became of one message. Every outcome except a cancelled run takes the message off the queue.</summary>
public enum MessageOutcome
{
    /// <summary>The delivery was claimed and handed over.</summary>
    Handled,

    /// <summary>Nothing to do: the delivery is not due (any more), is finished, or does not exist.</summary>
    Ignored,

    /// <summary>The message cannot be understood and is set aside in the poison queue.</summary>
    Poison,

    /// <summary>The delivery could not be claimed (the database failed). Its lease will run out and it is announced again.</summary>
    ClaimFailed,
}

/// <summary>
/// The work source of the RabbitMQ transport: workers are woken by messages that carry only a delivery id (see
/// ADR 0010). What a message may cause is decided by the database, through <see cref="DeliveryClaimStore.TryClaimAsync"/>,
/// exactly as for a polling worker; so a duplicate, late or invented message cannot make anything happen twice.
/// </summary>
public sealed class RabbitMqDeliverySource : IDeliverySource
{
    private readonly RabbitMqConnection _connection;
    private readonly DeliveryClaimStore _claims;
    private readonly DeliveryOptions _options;
    private readonly ILogger<RabbitMqDeliverySource> _logger;

    public RabbitMqDeliverySource(
        RabbitMqConnection connection,
        DeliveryClaimStore claims,
        IOptions<DeliveryOptions> options,
        ILogger<RabbitMqDeliverySource> logger)
    {
        _connection = connection;
        _claims = claims;
        _options = options.Value;
        _options.EnsureValid();
        _logger = logger;
    }

    public async Task RunAsync(Func<DeliveryClaim, CancellationToken, Task> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(handle, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // For example the broker is not there (yet). Keep trying: it is the database that keeps the work safe.
                _logger.LogError(ex, "Consuming delivery messages failed. Retrying in {Delay}.", _options.ErrorBackoff);

                try
                {
                    await Task.Delay(_options.ErrorBackoff, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Handles one message and returns what became of it. It never throws, except when <paramref name="ct"/> is
    /// cancelled in the middle of a delivery: then the message must not be acknowledged, so that another worker gets it.
    /// </summary>
    public async Task<MessageOutcome> HandleMessageAsync(
        ReadOnlyMemory<byte> body,
        Func<DeliveryClaim, CancellationToken, Task> handle,
        CancellationToken ct)
    {
        if (!DeliveryMessage.TryParse(body.Span, out var deliveryId))
        {
            _logger.LogWarning("A message that is not a delivery announcement was set aside ({Length} bytes).", body.Length);
            return MessageOutcome.Poison;
        }

        DeliveryClaim? claim;
        try
        {
            claim = await _claims.TryClaimAsync(deliveryId, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogError(ex, "Delivery {DeliveryId} could not be claimed. It will be announced again.", deliveryId);
            return MessageOutcome.ClaimFailed;
        }

        if (claim is null)
        {
            return MessageOutcome.Ignored;
        }

        try
        {
            await handle(claim, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // The attempt is recorded by the database (the delivery stays InProgress until its lock runs out), so
            // the message has done its job either way.
            _logger.LogError(ex, "Handling delivery {DeliveryId} failed.", deliveryId);
        }

        return MessageOutcome.Handled;
    }

    private async Task ConsumeAsync(Func<DeliveryClaim, CancellationToken, Task> handle, CancellationToken ct)
    {
        var connection = await _connection.GetAsync(ct);
        var queue = _connection.Options.Queue;

        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: (ushort)Math.Min(_options.MaxParallelism, ushort.MaxValue)),
            ct);

        // The broker hands out no more unacknowledged messages than this worker is willing to run at once.
        await channel.BasicQosAsync(0, (ushort)Math.Min(_options.MaxParallelism, ushort.MaxValue), global: false, ct);

        // Set when the broker closes the channel or cancels the consumer: the library cannot repair those by
        // itself (it does recover from a lost connection), so start over with a new channel.
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, args) =>
        {
            if (args.Initiator == ShutdownInitiator.Peer)
            {
                lost.TrySetException(new InvalidOperationException($"The broker closed the channel: {args.ReplyText}"));
            }

            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, message) =>
        {
            MessageOutcome outcome;
            try
            {
                outcome = await HandleMessageAsync(message.Body, handle, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // stopping: leave it unacknowledged
            }

            try
            {
                if (outcome == MessageOutcome.Poison)
                {
                    await channel.BasicNackAsync(message.DeliveryTag, multiple: false, requeue: false, CancellationToken.None);
                }
                else
                {
                    await channel.BasicAckAsync(message.DeliveryTag, multiple: false, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                // The channel is gone. The broker will deliver the message again; the claim will say Ignored.
                _logger.LogWarning(ex, "A message could not be acknowledged.");
            }
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);

        using var stopped = ct.Register(() => lost.TrySetCanceled(ct));
        await lost.Task;
    }
}
