using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// The part of a message transport that finds deliveries and announces them, tested with a publisher that is a fake
/// of the broker: what matters here is the order of things and what is left behind when the broker fails.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DeliveryRelayTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private readonly PostgresFixture _postgres;

    public DeliveryRelayTests(PostgresFixture postgres) => _postgres = postgres;

    private async Task<(DispatchHarness Harness, FakePublisher Publisher)> StartAsync(Action<DeliveryOptions>? configure = null)
    {
        var publisher = new FakePublisher();
        var harness = await DispatchHarness.CreateAsync(
            _postgres,
            o =>
            {
                o.EnqueueLease = Lease;
                o.PollInterval = TimeSpan.FromMilliseconds(20);
                o.ErrorBackoff = TimeSpan.FromMilliseconds(250);
                configure?.Invoke(o);
            },
            configureServices: services => services.AddSingleton<IDeliveryPublisher>(publisher));
        publisher.Harness = harness;
        return (harness, publisher);
    }

    // ---------- one pass ----------

    [Fact]
    public async Task What_is_due_is_leased_and_announced_exactly_once()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var first = await h.SeedAsync("http://unused.example/1");
        var second = await h.SeedAsync("http://unused.example/2");

        var result = await h.Relay.RelayOnceAsync(default);

        Assert.Equal(new RelayResult(Leased: 2, Failed: 0), result);
        Assert.Equal([first.DeliveryId, second.DeliveryId], publisher.Published.OrderBy(i => i == first.DeliveryId ? 0 : 1));
        Assert.Equal(2, publisher.Published.Count);
        Assert.NotNull((await h.GetDeliveryAsync(first.DeliveryId)).EnqueuedUntil);
    }

    [Fact]
    public async Task With_nothing_due_nothing_is_announced_and_the_publisher_is_not_even_asked()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;

        var result = await h.Relay.RelayOnceAsync(default);

        Assert.Equal(new RelayResult(0, 0), result);
        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task A_delivery_is_not_announced_again_while_its_lease_lasts_and_is_when_it_has_run_out()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        await h.SeedAsync("http://unused.example/1");

        await h.Relay.RelayOnceAsync(default);
        await h.Relay.RelayOnceAsync(default);
        Assert.Single(publisher.Published);

        h.Clock.Advance(Lease); // the message may have been lost
        await h.Relay.RelayOnceAsync(default);
        Assert.Equal(2, publisher.Published.Count);
    }

    [Fact]
    public async Task Announcing_changes_nothing_about_what_the_delivery_is()
    {
        var (h, _) = await StartAsync();
        await using var __ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");

        await h.Relay.RelayOnceAsync(default);

        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
    }

    // ---------- the order: lease first, then announce ----------

    [Fact]
    public async Task The_lease_is_already_committed_when_the_message_is_sent()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        DateTimeOffset? leaseSeenWhilePublishing = null;
        publisher.OnPublish = async ids => leaseSeenWhilePublishing = (await h.GetDeliveryAsync(ids[0])).EnqueuedUntil;

        await h.Relay.RelayOnceAsync(default);

        Assert.Equal(h.Clock.GetUtcNow() + Lease, leaseSeenWhilePublishing);
    }

    [Fact]
    public async Task A_consumer_that_acts_on_the_message_at_once_is_not_made_to_wait_or_to_miss_it()
    {
        // The race that ADR 0010 first got wrong: if the lease were still being written in an open transaction when
        // the message went out, a consumer that skips locked rows would find the row locked, drop the message, and
        // the delivery would wait for the whole lease. Here the "consumer" claims inside the publish call itself.
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        DeliveryClaim? claimedByConsumer = null;
        publisher.OnPublish = async ids => claimedByConsumer = await h.Claims.TryClaimAsync(ids[0], default);

        await h.Relay.RelayOnceAsync(default);

        Assert.Equal(new DeliveryClaim(seeded.DeliveryId, 1), claimedByConsumer);
    }

    [Fact]
    public async Task No_row_is_locked_while_the_message_is_being_sent()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        await h.SeedAsync("http://unused.example/1");
        var otherRelayLeased = -1;
        publisher.OnPublish = async _ => otherRelayLeased = (await h.Enqueue.LeaseDueAsync(default)).Count;

        await h.Relay.RelayOnceAsync(default);

        // Another relay running at the same moment is not blocked behind a lock (it simply finds nothing to lease,
        // because the row already has one).
        Assert.Equal(0, otherRelayLeased);
    }

    // ---------- when announcing fails ----------

    [Fact]
    public async Task A_delivery_the_broker_did_not_confirm_gets_its_lease_back_and_the_others_keep_theirs()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var kept = await h.SeedAsync("http://unused.example/1");
        var failed = await h.SeedAsync("http://unused.example/2");
        publisher.Unconfirmed = [failed.DeliveryId];

        var result = await h.Relay.RelayOnceAsync(default);

        Assert.Equal(new RelayResult(Leased: 2, Failed: 1), result);
        Assert.NotNull((await h.GetDeliveryAsync(kept.DeliveryId)).EnqueuedUntil);
        Assert.Null((await h.GetDeliveryAsync(failed.DeliveryId)).EnqueuedUntil);
    }

    [Fact]
    public async Task When_the_publisher_fails_altogether_every_lease_is_given_back_and_nothing_is_thrown()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var one = await h.SeedAsync("http://unused.example/1");
        var two = await h.SeedAsync("http://unused.example/2");
        publisher.Throw = new InvalidOperationException("the broker is not there");

        var result = await h.Relay.RelayOnceAsync(default);

        Assert.Equal(new RelayResult(Leased: 2, Failed: 2), result);
        Assert.Null((await h.GetDeliveryAsync(one.DeliveryId)).EnqueuedUntil);
        Assert.Null((await h.GetDeliveryAsync(two.DeliveryId)).EnqueuedUntil);
        Assert.Contains(h.Logs, line => line.Level == LogLevel.Error && line.Message.Contains("could not be announced"));
    }

    [Fact]
    public async Task Deliveries_whose_announcement_failed_are_due_again_at_once()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        publisher.Throw = new InvalidOperationException("down");
        await h.Relay.RelayOnceAsync(default);

        publisher.Throw = null; // the broker is back, with no time having passed
        var result = await h.Relay.RelayOnceAsync(default);

        Assert.Equal(new RelayResult(1, 0), result);
        Assert.Equal([seeded.DeliveryId], publisher.Published);
    }

    [Fact]
    public async Task Stopping_while_announcing_gives_the_leases_back_so_a_restart_does_not_wait_for_them()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        using var cts = new CancellationTokenSource();
        publisher.OnPublish = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Relay.RelayOnceAsync(cts.Token));

        Assert.Null((await h.GetDeliveryAsync(seeded.DeliveryId)).EnqueuedUntil);
    }

    // ---------- the loop ----------

    [Fact]
    public async Task The_running_relay_announces_work_that_appears_later()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        using var cts = new CancellationTokenSource();
        var running = h.Relay.RunAsync(cts.Token);

        await Task.Delay(150);
        var seeded = await h.SeedAsync("http://unused.example/1");
        var announced = await WaitForAsync(() => publisher.Published.Contains(seeded.DeliveryId));
        cts.Cancel();
        await running;

        Assert.True(announced, "the relay did not announce work that appeared while it was running");
    }

    [Fact]
    public async Task A_failing_broker_is_not_hammered_the_relay_pauses_between_tries()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        await h.SeedAsync("http://unused.example/1");
        publisher.Throw = new InvalidOperationException("down");
        using var cts = new CancellationTokenSource();
        var before = h.Clock.Reads;

        var running = h.Relay.RunAsync(cts.Token);
        await Task.Delay(1_000);
        cts.Cancel();
        await running;

        // A pause of 250 ms allows about four rounds in a second. Without the pause it would be thousands.
        Assert.InRange(publisher.Calls, 2, 10);
        Assert.InRange(h.Clock.Reads - before, 2, 20);
    }

    [Fact]
    public async Task An_idle_relay_rests_between_looks_at_the_database()
    {
        var (h, _) = await StartAsync(o => o.PollInterval = TimeSpan.FromMilliseconds(250));
        await using var _ = h;
        using var cts = new CancellationTokenSource();
        var before = h.Clock.Reads;

        var running = h.Relay.RunAsync(cts.Token);
        await Task.Delay(1_000);
        cts.Cancel();
        await running;

        // About four looks in a second; without the rest it would be thousands.
        Assert.InRange(h.Clock.Reads - before, 1, 12);
    }

    [Fact]
    public async Task Work_is_announced_after_the_broker_comes_back_and_nothing_is_lost()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        publisher.Throw = new InvalidOperationException("down");
        using var cts = new CancellationTokenSource();
        var running = h.Relay.RunAsync(cts.Token);
        await Task.Delay(400);

        publisher.Throw = null;
        var announced = await WaitForAsync(() => publisher.Published.Contains(seeded.DeliveryId));
        cts.Cancel();
        await running;

        Assert.True(announced);
    }

    [Fact]
    public async Task A_failure_to_reach_the_database_does_not_stop_the_relay()
    {
        var (h, publisher) = await StartAsync();
        await using var _ = h;
        var seeded = await h.SeedAsync("http://unused.example/1");
        await h.ExecuteSqlAsync("ALTER TABLE deliveries RENAME TO deliveries_away");
        using var cts = new CancellationTokenSource();
        var running = h.Relay.RunAsync(cts.Token);
        await Task.Delay(400);
        Assert.Empty(publisher.Published);

        await h.ExecuteSqlAsync("ALTER TABLE deliveries_away RENAME TO deliveries");
        var announced = await WaitForAsync(() => publisher.Published.Contains(seeded.DeliveryId));
        cts.Cancel();
        await running;

        Assert.True(announced, "the relay did not recover after the database came back");
    }

    [Fact]
    public async Task Stopping_a_resting_relay_returns_promptly_and_is_not_logged_as_an_error()
    {
        var (h, _) = await StartAsync(o => o.PollInterval = TimeSpan.FromHours(1));
        await using var _ = h;
        using var cts = new CancellationTokenSource();
        var running = h.Relay.RunAsync(cts.Token);
        await Task.Delay(200);

        cts.Cancel();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(running, finished);
        await running;
        Assert.DoesNotContain(h.Logs, line => line.Level >= LogLevel.Error && line.Category.StartsWith("WebhookService"));
    }

    [Fact]
    public async Task Stopping_during_the_pause_after_a_failure_returns_promptly_too()
    {
        var (h, publisher) = await StartAsync(o => o.ErrorBackoff = TimeSpan.FromHours(1));
        await using var _ = h;
        await h.SeedAsync("http://unused.example/1");
        publisher.Throw = new InvalidOperationException("down");
        using var cts = new CancellationTokenSource();
        var running = h.Relay.RunAsync(cts.Token);
        await Task.Delay(300);

        cts.Cancel();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(running, finished);
        await running;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
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

    /// <summary>Stands in for the broker: remembers what it was asked to announce, and can be made to fail.</summary>
    private sealed class FakePublisher : IDeliveryPublisher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _published = new();
        private int _calls;

        public DispatchHarness? Harness { get; set; }

        public IReadOnlyCollection<Guid> Published => _published.ToList();

        public int Calls => Volatile.Read(ref _calls);

        public Exception? Throw { get; set; }

        public IReadOnlyCollection<Guid> Unconfirmed { get; set; } = [];

        public Func<IReadOnlyList<Guid>, Task>? OnPublish { get; set; }

        public async Task<IReadOnlyList<Guid>> PublishAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);

            if (Throw is not null)
            {
                throw Throw;
            }

            if (OnPublish is not null)
            {
                await OnPublish(deliveryIds);
            }

            var failed = deliveryIds.Where(Unconfirmed.Contains).ToList();
            foreach (var id in deliveryIds.Except(failed))
            {
                _published.Enqueue(id);
            }

            return failed;
        }
    }
}
