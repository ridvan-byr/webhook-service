using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// The transport with a message broker: a relay announces due deliveries, workers act on the messages. It runs every
/// test of <see cref="DeliveryTransportContract"/> against a real RabbitMQ and a real PostgreSQL, and then the tests
/// of what only a broker can do wrong.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public class RabbitMqTransportTests : DeliveryTransportContract
{
    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _broker;

    public RabbitMqTransportTests(PostgresFixture postgres, RabbitMqFixture broker)
    {
        _postgres = postgres;
        _broker = broker;
    }

    protected override Task<DispatchHarness> CreateHarnessAsync(Action<DeliveryOptions>? configure = null) =>
        CreateAsync(_postgres, _broker.CreateOptions(), configure);

    private static Task<DispatchHarness> CreateAsync(PostgresFixture postgres, RabbitMqOptions broker, Action<DeliveryOptions>? configure) =>
        DispatchHarness.CreateAsync(
            postgres,
            o =>
            {
                o.Transport = DeliveryTransport.RabbitMq;
                o.EnqueueLease = TimeSpan.FromMinutes(2);
                configure?.Invoke(o);
            },
            configureServices: services => RabbitMqTestHost.AddBroker(services, broker),
            driverFactory: h => new BatchDriver(ct => OneBatchAsync(h, ct)));

    /// <summary>
    /// One batch of this transport: the relay announces what is due, then the worker takes every message that is
    /// waiting and handles them, up to MaxParallelism at a time, as the running consumer would.
    /// </summary>
    private static async Task<int> OneBatchAsync(DispatchHarness h, CancellationToken ct)
    {
        await h.Relay.RelayOnceAsync(ct);

        var source = h.Get<RabbitMqDeliverySource>();
        var processor = h.Get<DeliveryProcessor>();
        var options = h.Get<IOptions<DeliveryOptions>>().Value;
        var queue = h.Get<IOptions<RabbitMqOptions>>().Value.Queue;

        var connection = await h.Get<RabbitMqConnection>().GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        var messages = new List<BasicGetResult>();
        while (await channel.BasicGetAsync(queue, autoAck: false, ct) is { } message)
        {
            messages.Add(message);
        }

        var handled = 0;
        var outcomes = new MessageOutcome[messages.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, messages.Count),
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelism, CancellationToken = ct },
            async (index, token) =>
            {
                outcomes[index] = await source.HandleMessageAsync(messages[index].Body, processor.DeliverAsync, token);
                if (outcomes[index] == MessageOutcome.Handled)
                {
                    Interlocked.Increment(ref handled);
                }
            });

        for (var i = 0; i < messages.Count; i++)
        {
            await channel.BasicAckAsync(messages[i].DeliveryTag, multiple: false, ct);
        }

        return handled;
    }

    private async Task<(DispatchHarness Harness, RabbitMqOptions Broker)> StartAsync(Action<DeliveryOptions>? configure = null)
    {
        var broker = _broker.CreateOptions();
        return (await CreateAsync(_postgres, broker, configure), broker);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, int seconds = 30)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    // ---------- what only a broker can do wrong ----------

    [Fact]
    public async Task A_message_that_was_lost_is_announced_again_when_the_lease_has_run_out()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var (h, broker) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");
        await h.Relay.RelayOnceAsync(default);
        await using (var raw = await _broker.OpenRawConnectionAsync(broker))
        await using (var channel = await raw.CreateChannelAsync())
        {
            await channel.QueuePurgeAsync(broker.Queue); // the message is gone, as if the broker had lost it
        }

        // While the lease lasts, the delivery is left alone: there is no sign anything is wrong yet.
        Assert.Equal(0, await h.Processor.ProcessBatchAsync(default));

        h.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, await h.Processor.ProcessBatchAsync(default));
        Assert.Equal(DeliveryStatus.Succeeded, (await h.GetDeliveryAsync(seeded.DeliveryId)).Status);
    }

    [Fact]
    public async Task A_delivery_that_a_polling_worker_took_first_is_not_attempted_again_when_its_message_arrives()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var (h, _) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");
        await h.Relay.RelayOnceAsync(default);

        // Both kinds of worker run over one database (ADR 0010): the polling one gets there first.
        var polled = await h.Get<PostgresDeliverySource>()
            .ProcessBatchAsync(h.Get<DeliveryProcessor>().DeliverAsync, default);

        Assert.Equal(1, polled);
        Assert.Equal(0, await h.Processor.ProcessBatchAsync(default)); // the message is taken and ignored
        Assert.Single(receiver.Requests);
        Assert.Equal(1, (await h.GetDeliveryAsync(seeded.DeliveryId)).AttemptCount);
    }

    [Fact]
    public async Task Events_accepted_while_the_broker_is_down_are_delivered_after_it_comes_back()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var (h, _) = await StartAsync(o => o.ErrorBackoff = TimeSpan.FromMilliseconds(250));
        await using var _ = h;
        await h.Relay.RelayOnceAsync(default); // connected, topology declared
        await _broker.StopBrokerAsync();
        try
        {
            var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok"); // the API keeps accepting events

            var result = await h.Relay.RelayOnceAsync(default);

            Assert.Equal(new RelayResult(Leased: 1, Failed: 1), result);
            Assert.Null((await h.GetDeliveryAsync(seeded.DeliveryId)).EnqueuedUntil); // due again, nothing is stuck
        }
        finally
        {
            await _broker.StartBrokerAsync();
        }

        var delivered = await WaitForAsync(async () =>
        {
            try
            {
                await h.Processor.ProcessBatchAsync(default);
            }
            catch
            {
                // The connection is still recovering.
            }

            return receiver.Requests.Count == 1;
        });

        Assert.True(delivered, "the delivery was not made after the broker came back");
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task Two_workers_on_one_queue_never_attempt_the_same_delivery_twice()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var (first, _) = await StartAsync(o => o.MaxParallelism = 4);
        await using var _ = first;
        for (var i = 0; i < 12; i++)
        {
            await first.SeedAsync($"{receiver.BaseUrl}/ok");
        }

        await using var second = first.CreateSecondWorker();
        var recorder = new System.Collections.Concurrent.ConcurrentQueue<DeliveryClaim>();
        Task Record(DeliveryClaim claim, CancellationToken ct)
        {
            recorder.Enqueue(claim);
            return Task.CompletedTask;
        }

        using var cts = new CancellationTokenSource();
        var runs = new[]
        {
            first.Get<RabbitMqDeliverySource>().RunAsync(Record, cts.Token),
            second.Get<RabbitMqDeliverySource>().RunAsync(Record, cts.Token),
        };
        await first.Relay.RelayOnceAsync(default);
        await first.Relay.RelayOnceAsync(default);

        var done = await WaitForAsync(() => Task.FromResult(recorder.Count >= 12), 30);
        await Task.Delay(500); // anything extra would show up now
        cts.Cancel();
        await Task.WhenAll(runs);

        Assert.True(done);
        Assert.Equal(12, recorder.Count);
        Assert.Equal(12, recorder.Select(c => c.DeliveryId).Distinct().Count());
        Assert.All(recorder, claim => Assert.Equal(1, claim.AttemptNumber));
    }
}
