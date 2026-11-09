using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using WebhookService.Infrastructure.Dispatch.RabbitMq;

namespace WebhookService.Tests.Dispatch;

/// <summary>The publisher and the topology, against a real broker.</summary>
[Collection(RabbitMqCollection.Name)]
public class RabbitMqPublisherTests
{
    private readonly RabbitMqFixture _broker;

    public RabbitMqPublisherTests(RabbitMqFixture broker) => _broker = broker;

    private static RabbitMqDeliveryPublisher PublisherFor(RabbitMqConnection connection) =>
        new(connection, NullLogger<RabbitMqDeliveryPublisher>.Instance);

    private static RabbitMqConnection Connect(RabbitMqOptions options) => new(Options.Create(options));

    private static async Task<BasicGetResult?> GetAsync(IChannel channel, string queue)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: false);
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(50);
        }

        return null;
    }

    [Fact]
    public async Task Published_deliveries_arrive_in_the_queue_as_persistent_messages_that_carry_only_the_id()
    {
        var options = _broker.CreateOptions();
        await using var connection = Connect(options);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var failed = await PublisherFor(connection).PublishAsync(ids, default);

        Assert.Empty(failed);
        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();
        var received = new List<Guid>();
        for (var i = 0; i < ids.Length; i++)
        {
            var message = await GetAsync(channel, options.Queue);
            Assert.NotNull(message);
            Assert.Equal(DeliveryModes.Persistent, message.BasicProperties.DeliveryMode);
            Assert.True(DeliveryMessage.TryParse(message.Body.Span, out var id));
            Assert.Equal(DeliveryMessage.Create(id), message.Body.ToArray());
            received.Add(id);
            await channel.BasicAckAsync(message.DeliveryTag, false);
        }

        Assert.Equal(ids.OrderBy(i => i), received.OrderBy(i => i));
    }

    [Fact]
    public async Task Declaring_the_topology_again_from_another_process_changes_nothing()
    {
        var options = _broker.CreateOptions();
        await using var first = Connect(options);
        await using var second = Connect(options);
        var id = Guid.NewGuid();

        await PublisherFor(first).PublishAsync([id], default);
        await PublisherFor(second).PublishAsync([Guid.NewGuid()], default);

        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();
        var declared = await channel.QueueDeclarePassiveAsync(options.Queue);
        Assert.Equal(2u, declared.MessageCount);
    }

    [Fact]
    public async Task The_queues_survive_what_a_broker_restart_would_do_they_are_durable_and_quorum()
    {
        var options = _broker.CreateOptions();
        await using var connection = Connect(options);
        await PublisherFor(connection).PublishAsync([Guid.NewGuid()], default);

        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();

        // Declaring again with the same arguments succeeds only if the queue really has them; a transient or
        // classic queue would make the broker refuse (PRECONDITION_FAILED).
        await channel.QueueDeclareAsync(
            options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = options.PoisonQueue,
            });
    }

    [Fact]
    public async Task A_message_that_is_rejected_without_being_put_back_ends_up_in_the_poison_queue()
    {
        var options = _broker.CreateOptions();
        await using var connection = Connect(options);
        await PublisherFor(connection).PublishAsync([Guid.NewGuid()], default);
        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();
        var message = await GetAsync(channel, options.Queue);
        Assert.NotNull(message);

        await channel.BasicNackAsync(message.DeliveryTag, multiple: false, requeue: false);

        var poisoned = await GetAsync(channel, options.PoisonQueue);
        Assert.NotNull(poisoned);
        Assert.Null(await channel.BasicGetAsync(options.Queue, autoAck: true));
    }

    [Fact]
    public async Task A_message_the_broker_cannot_route_is_not_reported_as_announced()
    {
        var options = _broker.CreateOptions();
        await using var connection = Connect(options);
        await PublisherFor(connection).PublishAsync([Guid.NewGuid()], default); // creates the topology
        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using (var channel = await raw.CreateChannelAsync())
        {
            await channel.QueueUnbindAsync(options.Queue, options.Exchange, options.Queue);
        }

        // Without "mandatory" the broker would silently drop it and the lease would run out for nothing.
        await Assert.ThrowsAnyAsync<Exception>(() => PublisherFor(connection).PublishAsync([Guid.NewGuid()], default));
    }

    [Fact]
    public async Task A_broker_that_cannot_be_reached_is_an_error_and_does_not_hang()
    {
        var options = _broker.CreateOptions();
        options.Port = 1; // nothing listens here
        await using var connection = Connect(options);

        var publishing = PublisherFor(connection).PublishAsync([Guid.NewGuid()], default);
        var finished = await Task.WhenAny(publishing, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(publishing, finished);
        await Assert.ThrowsAnyAsync<Exception>(() => publishing);
    }

    [Fact]
    public async Task A_broker_that_stops_answering_is_given_up_on_after_the_publish_timeout()
    {
        var options = _broker.CreateOptions();
        options.PublishTimeout = TimeSpan.FromSeconds(1);
        await using var connection = Connect(options);
        await PublisherFor(connection).PublishAsync([Guid.NewGuid()], default); // connected, topology declared

        // With its memory alarm raised the broker stops reading from publishers: no confirm will come.
        await _broker.SetMemoryWatermarkAsync("0");
        try
        {
            var publishing = PublisherFor(connection).PublishAsync([Guid.NewGuid()], default);
            var finished = await Task.WhenAny(publishing, Task.Delay(TimeSpan.FromSeconds(20)));

            Assert.Same(publishing, finished);
            await Assert.ThrowsAnyAsync<Exception>(() => publishing);
        }
        finally
        {
            await _broker.SetMemoryWatermarkAsync("0.4");
        }
    }

    [Fact]
    public async Task A_cancelled_publish_is_cancelled_and_not_reported_as_a_broker_failure()
    {
        var options = _broker.CreateOptions();
        await using var connection = Connect(options);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublisherFor(connection).PublishAsync([Guid.NewGuid()], cts.Token));
    }

    [Fact]
    public async Task A_connection_that_could_not_be_made_is_tried_again_next_time()
    {
        var options = _broker.CreateOptions();
        var goodPort = options.Port;
        options.Port = 1;
        await using var connection = Connect(options);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.GetAsync(default));

        options.Port = goodPort; // the same object is read again on the next try
        var opened = await connection.GetAsync(default);

        Assert.True(opened.IsOpen);
    }

    [Fact]
    public void Settings_that_are_not_valid_stop_the_connection_from_being_created()
    {
        Assert.Throws<InvalidOperationException>(() => Connect(new RabbitMqOptions()));
    }
}
