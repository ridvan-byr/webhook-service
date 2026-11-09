using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// The worker side of the RabbitMQ transport: what happens to a message when it arrives. The rules are that a message
/// never causes anything a polling worker would not do, is always taken off the queue (the database, not the queue,
/// remembers what is left to do), and that a message nobody can understand is set aside instead of being retried.
/// </summary>
[Collection(RabbitMqCollection.Name)]
public class RabbitMqConsumerTests
{
    private readonly PostgresFixture _postgres;
    private readonly RabbitMqFixture _broker;

    public RabbitMqConsumerTests(PostgresFixture postgres, RabbitMqFixture broker)
    {
        _postgres = postgres;
        _broker = broker;
    }

    private async Task<(DispatchHarness Harness, RabbitMqOptions Options)> StartAsync(Action<DeliveryOptions>? configure = null)
    {
        var options = _broker.CreateOptions();
        var harness = await RabbitMqTestHost.CreateAsync(_postgres, options, configure);
        return (harness, options);
    }

    private static RabbitMqDeliverySource SourceOf(DispatchHarness h) => h.Get<RabbitMqDeliverySource>();

    private sealed class Recorder
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<DeliveryClaim> _claims = new();
        private int _running;
        private int _maxRunning;

        public IReadOnlyCollection<DeliveryClaim> Claims => _claims.ToList();

        public int MaxRunning => Volatile.Read(ref _maxRunning);

        public Func<DeliveryClaim, CancellationToken, Task>? Behaviour { get; set; }

        public async Task HandleAsync(DeliveryClaim claim, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen)
            {
            }

            try
            {
                _claims.Enqueue(claim);
                if (Behaviour is not null)
                {
                    await Behaviour(claim, ct);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    // ---------- one message ----------

    [Fact]
    public async Task A_message_for_a_due_delivery_makes_the_worker_attempt_it()
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        var recorder = new Recorder();

        var outcome = await SourceOf(h).HandleMessageAsync(DeliveryMessage.Create(seeded.DeliveryId), recorder.HandleAsync, default);

        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Equal([new DeliveryClaim(seeded.DeliveryId, 1)], recorder.Claims);
    }

    [Fact]
    public async Task A_message_for_a_delivery_that_does_not_exist_is_dropped_quietly()
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        var recorder = new Recorder();

        var outcome = await SourceOf(h).HandleMessageAsync(DeliveryMessage.Create(Guid.NewGuid()), recorder.HandleAsync, default);

        Assert.Equal(MessageOutcome.Ignored, outcome);
        Assert.Empty(recorder.Claims);
    }

    [Fact]
    public async Task A_message_for_a_delivery_that_is_already_finished_does_nothing()
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        await h.ModifyDeliveryAsync(seeded.DeliveryId, d =>
        {
            d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            d.MarkSucceeded();
        });
        var recorder = new Recorder();

        var outcome = await SourceOf(h).HandleMessageAsync(DeliveryMessage.Create(seeded.DeliveryId), recorder.HandleAsync, default);

        Assert.Equal(MessageOutcome.Ignored, outcome);
        Assert.Empty(recorder.Claims);
        Assert.Equal(DeliveryStatus.Succeeded, (await h.GetDeliveryAsync(seeded.DeliveryId)).Status);
    }

    [Fact]
    public async Task The_same_message_arriving_twice_makes_only_one_attempt()
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        var recorder = new Recorder();
        var body = DeliveryMessage.Create(seeded.DeliveryId);

        var first = await SourceOf(h).HandleMessageAsync(body, recorder.HandleAsync, default);
        var second = await SourceOf(h).HandleMessageAsync(body, recorder.HandleAsync, default);

        Assert.Equal(MessageOutcome.Handled, first);
        Assert.Equal(MessageOutcome.Ignored, second);
        Assert.Single(recorder.Claims);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"deliveryId\":\"nope\"}")]
    [InlineData("{\"deliveryId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task A_message_that_cannot_be_understood_is_poison_and_nothing_is_attempted(string text)
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        await h.SeedAsync("http://unused.example/1");
        var recorder = new Recorder();

        var outcome = await SourceOf(h).HandleMessageAsync(System.Text.Encoding.UTF8.GetBytes(text), recorder.HandleAsync, default);

        Assert.Equal(MessageOutcome.Poison, outcome);
        Assert.Empty(recorder.Claims);
    }

    [Fact]
    public async Task A_handler_that_fails_does_not_make_the_message_come_back()
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        var recorder = new Recorder { Behaviour = (_, _) => throw new InvalidOperationException("boom") };

        var outcome = await SourceOf(h).HandleMessageAsync(DeliveryMessage.Create(seeded.DeliveryId), recorder.HandleAsync, default);

        // Retrying is the database's business (the delivery is InProgress until its lock runs out, then due again);
        // a message that came back at once would only hammer a receiver that is failing.
        Assert.Equal(MessageOutcome.Handled, outcome);
        Assert.Contains(h.Logs, line => line.Level == LogLevel.Error && line.Message.Contains(seeded.DeliveryId.ToString()));
    }

    [Fact]
    public async Task Stopping_in_the_middle_of_a_delivery_is_not_swallowed()
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder
        {
            Behaviour = (_, _) =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };

        // The message must not be acknowledged: another worker should get it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SourceOf(h).HandleMessageAsync(DeliveryMessage.Create(seeded.DeliveryId), recorder.HandleAsync, cts.Token));
    }

    [Fact]
    public async Task When_the_database_cannot_be_reached_the_message_is_dropped_and_the_failure_is_logged()
    {
        var (h, _) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        await h.ExecuteSqlAsync("ALTER TABLE deliveries RENAME TO deliveries_away");
        var recorder = new Recorder();

        var outcome = await SourceOf(h).HandleMessageAsync(DeliveryMessage.Create(seeded.DeliveryId), recorder.HandleAsync, default);

        // Putting it back would spin for as long as the database is away. The delivery's lease runs out and the
        // relay announces it again.
        Assert.Equal(MessageOutcome.ClaimFailed, outcome);
        Assert.Empty(recorder.Claims);
        Assert.Contains(h.Logs, line => line.Level == LogLevel.Error && line.Category.StartsWith("WebhookService"));
    }

    // ---------- with the broker ----------

    private static async Task<bool> WaitForAsync(Func<bool> condition, int seconds = 15)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private async Task PublishRawAsync(RabbitMqOptions options, byte[] body)
    {
        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync(new CreateChannelOptions(true, true));
        await channel.BasicPublishAsync(options.Exchange, options.Queue, true, new BasicProperties { Persistent = true }, body);
    }

    private static async Task<uint> CountAsync(IChannel channel, string queue) => (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;

    [Fact]
    public async Task Announced_deliveries_are_attempted_by_the_running_consumer_and_the_queue_is_left_empty()
    {
        var (h, options) = await StartAsync();
        await using var _ = h;
        var seeded = new[] { await h.SeedAsync("http://unused.example/1"), await h.SeedAsync("http://unused.example/2") };
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        var running = SourceOf(h).RunAsync(recorder.HandleAsync, cts.Token);

        await h.Relay.RelayOnceAsync(default);
        var handled = await WaitForAsync(() => recorder.Claims.Count == 2);
        cts.Cancel();
        await running;

        Assert.True(handled, "the consumer did not receive the announcements");
        Assert.Equal(seeded.Select(s => s.DeliveryId).Order(), recorder.Claims.Select(c => c.DeliveryId).Order());
        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();
        Assert.Equal(0u, await CountAsync(channel, options.Queue));
        Assert.Equal(0u, await CountAsync(channel, options.PoisonQueue));
    }

    [Fact]
    public async Task A_message_that_cannot_be_understood_goes_to_the_poison_queue_and_the_consumer_carries_on()
    {
        var (h, options) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        var running = SourceOf(h).RunAsync(recorder.HandleAsync, cts.Token);
        await h.Get<RabbitMqConnection>().GetAsync(default); // the exchange and the queues exist once connected
        await PublishRawAsync(options, "this is not a delivery"u8.ToArray());

        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();
        var parked = await WaitForAsync(() => CountAsync(channel, options.PoisonQueue).GetAwaiter().GetResult() == 1);
        await h.Relay.RelayOnceAsync(default); // a good message after the bad one
        var handled = await WaitForAsync(() => recorder.Claims.Count == 1);
        cts.Cancel();
        await running;

        Assert.True(parked, "the poison message was not set aside");
        Assert.True(handled, "the consumer did not carry on after a poison message");
        Assert.Equal(seeded.DeliveryId, recorder.Claims.Single().DeliveryId);
        Assert.Equal(0u, await CountAsync(channel, options.Queue));
    }

    [Fact]
    public async Task No_more_deliveries_run_at_the_same_time_than_the_parallelism_allows()
    {
        var (h, options) = await StartAsync(o => o.MaxParallelism = 3);
        await using var _ = h;
        for (var i = 0; i < 8; i++)
        {
            await h.SeedAsync($"http://unused.example/{i}");
        }

        var recorder = new Recorder { Behaviour = async (_, ct) => await Task.Delay(300, ct) };
        using var cts = new CancellationTokenSource();
        var running = SourceOf(h).RunAsync(recorder.HandleAsync, cts.Token);

        await h.Relay.RelayOnceAsync(default);
        var handled = await WaitForAsync(() => recorder.Claims.Count == 8, 30);
        cts.Cancel();
        await running;

        Assert.True(handled);
        Assert.Equal(3, recorder.MaxRunning);
    }

    [Fact]
    public async Task The_broker_hands_a_worker_no_more_messages_than_it_may_run_at_once()
    {
        var (h, options) = await StartAsync(o => o.MaxParallelism = 2);
        await using var _ = h;
        for (var i = 0; i < 6; i++)
        {
            await h.SeedAsync($"http://unused.example/{i}");
        }

        var recorder = new Recorder { Behaviour = async (_, ct) => await Task.Delay(Timeout.Infinite, ct) };
        using var cts = new CancellationTokenSource();
        var running = SourceOf(h).RunAsync(recorder.HandleAsync, cts.Token);
        await h.Relay.RelayOnceAsync(default);
        Assert.True(await WaitForAsync(() => recorder.Claims.Count == 2));
        await Task.Delay(500);

        // Two are with the worker (unacknowledged); the other four wait in the queue for someone with room.
        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();
        var waiting = await CountAsync(channel, options.Queue);
        cts.Cancel();
        await running;

        Assert.Equal(4u, waiting);
    }

    [Fact]
    public async Task Stopping_returns_promptly_and_a_delivery_in_the_middle_of_being_attempted_is_left_for_another_worker()
    {
        var (h, options) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        var started = new TaskCompletionSource();
        var recorder = new Recorder
        {
            Behaviour = async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // a receiver that never answers
            },
        };
        using var cts = new CancellationTokenSource();
        var running = SourceOf(h).RunAsync(recorder.HandleAsync, cts.Token);
        await h.Relay.RelayOnceAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        cts.Cancel();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(running, finished);
        await running;
        // Not acknowledged, so the broker has it again; the delivery itself is InProgress until its lock runs out.
        await using var raw = await _broker.OpenRawConnectionAsync(options);
        await using var channel = await raw.CreateChannelAsync();
        Assert.True(await WaitForAsync(() => CountAsync(channel, options.Queue).GetAwaiter().GetResult() == 1));
        Assert.Equal(DeliveryStatus.InProgress, (await h.GetDeliveryAsync(seeded.DeliveryId)).Status);
    }

    [Fact]
    public async Task A_consumer_that_started_while_the_broker_was_away_connects_when_it_returns()
    {
        var (h, options) = await StartAsync();
        await using var _ = h;
        await h.SeedAsync("http://unused.example/1");
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        var wrongPort = options.Port;
        h.Get<IOptions<RabbitMqOptions>>().Value.Port = 1; // nothing listens there: the broker is "away"
        var running = SourceOf(h).RunAsync(recorder.HandleAsync, cts.Token);
        await Task.Delay(500);
        Assert.False(running.IsCompleted, "the consumer gave up instead of waiting for the broker");

        h.Get<IOptions<RabbitMqOptions>>().Value.Port = wrongPort; // the broker is back
        await h.Relay.RelayOnceAsync(default);
        var handled = await WaitForAsync(() => recorder.Claims.Count == 1, 30);
        cts.Cancel();
        await running;

        Assert.True(handled, "the consumer did not recover once the broker was back");
    }

    [Fact]
    public async Task A_consumer_keeps_working_after_the_broker_is_restarted()
    {
        var (h, _) = await StartAsync(o => o.ErrorBackoff = TimeSpan.FromMilliseconds(250));
        await using var _ = h;
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        var running = SourceOf(h).RunAsync(recorder.HandleAsync, cts.Token);
        var first = await h.SeedAsync("http://unused.example/1");
        await h.Relay.RelayOnceAsync(default);
        Assert.True(await WaitForAsync(() => recorder.Claims.Count == 1));

        await _broker.StopBrokerAsync();
        await Task.Delay(500);
        await _broker.StartBrokerAsync();

        var second = await h.SeedAsync("http://unused.example/2");
        var announced = await WaitForAsync(() =>
        {
            try
            {
                h.Relay.RelayOnceAsync(default).GetAwaiter().GetResult();
            }
            catch
            {
                // The broker is still coming back.
            }

            return recorder.Claims.Any(c => c.DeliveryId == second.DeliveryId);
        }, 60);
        cts.Cancel();
        await running;

        Assert.True(announced, "the consumer did not recover after the broker was restarted");
    }
}
