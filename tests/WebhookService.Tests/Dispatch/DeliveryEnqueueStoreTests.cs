using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// Finding the deliveries that should be announced, and putting a lease on them so they are not announced again
/// and again. Nothing is announced here: this is the database half of a transport that works with messages.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DeliveryEnqueueStoreTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private readonly PostgresFixture _postgres;

    public DeliveryEnqueueStoreTests(PostgresFixture postgres) => _postgres = postgres;

    private Task<DispatchHarness> StartAsync(Action<DeliveryOptions>? configure = null) =>
        DispatchHarness.CreateAsync(_postgres, o =>
        {
            o.EnqueueLease = Lease;
            configure?.Invoke(o);
        });

    private static async Task<Delivery> ReadAsync(DispatchHarness h, Guid id) => await h.GetDeliveryAsync(id);

    // ---------- leasing ----------

    [Fact]
    public async Task A_due_delivery_is_leased_until_the_lease_ends_and_is_otherwise_left_as_it_was()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");

        var leased = await h.Enqueue.LeaseDueAsync(default);

        Assert.Equal([seeded.DeliveryId], leased);
        var delivery = await ReadAsync(h, seeded.DeliveryId);
        Assert.Equal(h.Clock.GetUtcNow() + Lease, delivery.EnqueuedUntil);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
    }

    [Fact]
    public async Task A_leased_delivery_is_not_handed_out_again_until_its_lease_has_run_out()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.Enqueue.LeaseDueAsync(default);

        Assert.Empty(await h.Enqueue.LeaseDueAsync(default));

        h.Clock.Advance(Lease - TimeSpan.FromSeconds(1));
        Assert.Empty(await h.Enqueue.LeaseDueAsync(default));

        // The message may have been lost, so once the lease is over it is announced again.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([seeded.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task A_retry_that_is_not_due_yet_is_not_leased_and_is_when_it_is()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/fail");
        await h.Processor.ProcessBatchAsync(default); // fails, and a retry is scheduled in the future
        var retryAt = (await ReadAsync(h, seeded.DeliveryId)).NextAttemptAt;

        Assert.Empty(await h.Enqueue.LeaseDueAsync(default));

        h.Clock.Advance(retryAt - h.Clock.GetUtcNow());
        Assert.Equal([seeded.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task Finished_deliveries_are_never_leased()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync(o => o.MaxAttempts = 1);
        await h.SeedAsync($"{receiver.BaseUrl}/ok");     // will succeed
        await h.SeedAsync($"{receiver.BaseUrl}/fail");   // will be dead after one attempt
        await h.Processor.ProcessBatchAsync(default);
        h.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Empty(await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task A_delivery_abandoned_by_a_crashed_worker_is_leased_once_its_lock_has_expired_and_not_before()
    {
        await using var h = await StartAsync(o => o.LockDuration = TimeSpan.FromMinutes(1));
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.ModifyDeliveryAsync(seeded.DeliveryId, d => d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1)));

        // A slow but healthy attempt is not announced a second time.
        Assert.Empty(await h.Enqueue.LeaseDueAsync(default));

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal([seeded.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task At_most_one_batch_is_leased_at_a_time_and_the_oldest_come_first()
    {
        await using var h = await StartAsync(o => o.BatchSize = 2);
        var first = await h.SeedAsync("http://unused.example/1");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await h.SeedAsync("http://unused.example/2");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var third = await h.SeedAsync("http://unused.example/3");
        h.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([first.DeliveryId, second.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
        Assert.Equal([third.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
        Assert.Empty(await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task A_delivery_that_someone_else_is_leasing_is_skipped_and_never_waited_for()
    {
        await using var h = await StartAsync();
        await h.SeedAsync("http://unused.example/ok");

        // Another relay is in the middle of leasing: its transaction holds the row's lock.
        await using var other = await h.LockDeliveriesAsync();
        var leasing = h.Enqueue.LeaseDueAsync(default);
        var finished = await Task.WhenAny(leasing, Task.Delay(TimeSpan.FromSeconds(5)));

        try
        {
            Assert.Same(leasing, finished);
            Assert.Empty(await leasing);
        }
        finally
        {
            await other.ReleaseAsync();
            try
            {
                await leasing;
            }
            catch
            {
                // Only the assertions above matter.
            }
        }
    }

    [Fact]
    public async Task Two_relays_never_lease_the_same_delivery()
    {
        const int total = 30;
        await using var first = await StartAsync(o => o.BatchSize = 3);
        await using var second = first.CreateSecondWorker();
        var seeded = new List<Guid>();
        for (var i = 0; i < total; i++)
        {
            seeded.Add((await first.SeedAsync("http://unused.example/ok")).DeliveryId);
        }

        async Task<List<Guid>> Drain(DispatchHarness relay)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var all = new List<Guid>();
            List<Guid> batch;
            while ((batch = (await relay.Enqueue.LeaseDueAsync(default)).ToList()).Count > 0)
            {
                all.AddRange(batch);
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Deliveries were still being leased after 30 seconds.");
                }
            }

            return all;
        }

        var results = await Task.WhenAll(Drain(first), Drain(second));

        var everyLease = results.SelectMany(r => r).ToList();
        Assert.Equal(total, everyLease.Count);
        Assert.Equal(seeded.OrderBy(i => i), everyLease.OrderBy(i => i));
    }

    // ---------- giving a lease back ----------

    [Fact]
    public async Task A_lease_that_is_given_back_can_be_taken_again_at_once()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.Enqueue.LeaseDueAsync(default);

        await h.Enqueue.ReleaseAsync([seeded.DeliveryId], default);

        Assert.Null((await ReadAsync(h, seeded.DeliveryId)).EnqueuedUntil);
        Assert.Equal([seeded.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task Giving_back_a_lease_does_not_touch_a_delivery_a_worker_has_claimed_in_the_meantime()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.Enqueue.LeaseDueAsync(default);
        await h.ModifyDeliveryAsync(seeded.DeliveryId, d => d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1)));

        await h.Enqueue.ReleaseAsync([seeded.DeliveryId], default);

        var delivery = await ReadAsync(h, seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.InProgress, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Null(delivery.EnqueuedUntil);
    }

    [Fact]
    public async Task Giving_back_nothing_or_something_that_does_not_exist_is_not_an_error()
    {
        await using var h = await StartAsync();

        await h.Enqueue.ReleaseAsync([], default);
        await h.Enqueue.ReleaseAsync([Guid.NewGuid()], default);
    }

    [Fact]
    public async Task Giving_back_only_touches_the_deliveries_it_was_given()
    {
        await using var h = await StartAsync();
        var kept = await h.SeedAsync("http://unused.example/1");
        var given = await h.SeedAsync("http://unused.example/2");
        await h.Enqueue.LeaseDueAsync(default);

        await h.Enqueue.ReleaseAsync([given.DeliveryId], default);

        Assert.NotNull((await ReadAsync(h, kept.DeliveryId)).EnqueuedUntil);
        Assert.Null((await ReadAsync(h, given.DeliveryId)).EnqueuedUntil);
    }

    // ---------- how it fits with the rest ----------

    [Fact]
    public async Task A_failed_attempt_ends_the_lease_so_the_retry_is_leased_when_it_is_due_even_if_the_old_lease_would_still_hold()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync(o => o.EnqueueLease = TimeSpan.FromHours(1));
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/fail");
        await h.Enqueue.LeaseDueAsync(default);

        await h.Processor.ProcessBatchAsync(default); // a polling worker claims it anyway, and it fails

        var afterFailure = await ReadAsync(h, seeded.DeliveryId);
        Assert.Null(afterFailure.EnqueuedUntil);
        h.Clock.Advance(afterFailure.NextAttemptAt - h.Clock.GetUtcNow());
        Assert.Equal([seeded.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task A_worker_that_polls_the_database_still_takes_a_leased_delivery()
    {
        // Workers of both kinds can run together over one database: a lease never stops a claim.
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");
        await h.Enqueue.LeaseDueAsync(default);

        Assert.Equal(1, await h.Processor.ProcessBatchAsync(default));

        Assert.Equal(DeliveryStatus.Succeeded, (await ReadAsync(h, seeded.DeliveryId)).Status);
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task A_manual_re_send_is_leased_at_once()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync(o => o.MaxAttempts = 1);
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/fail");
        await h.Processor.ProcessBatchAsync(default);
        Assert.Equal(DeliveryStatus.Dead, (await ReadAsync(h, seeded.DeliveryId)).Status);

        await h.ModifyDeliveryAsync(seeded.DeliveryId, d => d.Requeue(h.Clock.GetUtcNow()));

        Assert.Equal([seeded.DeliveryId], await h.Enqueue.LeaseDueAsync(default));
    }

    [Fact]
    public async Task The_relay_and_a_polling_worker_agree_on_what_is_due()
    {
        // The two queries are written separately, so this is what stops them drifting apart: whatever a polling worker
        // would claim in a given state of the table, and only that, is what is leased.
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync(o =>
        {
            o.MaxAttempts = 2;
            o.LockDuration = TimeSpan.FromMinutes(1);
            o.BatchSize = 100;
        });
        var url = $"{receiver.BaseUrl}/ok";

        var dueNow = await h.SeedAsync(url);
        var notDue = await h.SeedAsync(url);
        await h.ModifyDeliveryAsync(notDue.DeliveryId, d =>
        {
            d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            d.MarkFailed(h.Clock.GetUtcNow(), "boom", new RetryPolicy(5, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)), 1);
        });
        var inProgressValid = await h.SeedAsync(url);
        await h.ModifyDeliveryAsync(inProgressValid.DeliveryId, d => d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(10)));
        var inProgressExpired = await h.SeedAsync(url);
        await h.ModifyDeliveryAsync(inProgressExpired.DeliveryId, d => d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1)));
        var succeeded = await h.SeedAsync(url);
        await h.ModifyDeliveryAsync(succeeded.DeliveryId, d =>
        {
            d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            d.MarkSucceeded();
        });
        var dead = await h.SeedAsync(url);
        await h.ModifyDeliveryAsync(dead.DeliveryId, d =>
        {
            var policy = new RetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            d.MarkFailed(h.Clock.GetUtcNow(), "boom", policy, 1);
        });

        // Two minutes later: the one-minute lock has expired, the ten-minute lock and the ten-minute retry have not.
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        var leased = (await h.Enqueue.LeaseDueAsync(default)).ToHashSet();

        // What a polling worker takes from the same table: the real thing is run, and whatever had an attempt
        // counted for it was claimed. (The leases are cleared first, because a lease never stops a claim.)
        await h.ExecuteSqlAsync("UPDATE deliveries SET \"EnqueuedUntil\" = NULL");
        var before = (await h.GetAllDeliveriesAsync()).ToDictionary(d => d.Id, d => d.AttemptCount);
        await h.Processor.ProcessBatchAsync(default);
        var claimedByPolling = (await h.GetAllDeliveriesAsync())
            .Where(d => d.AttemptCount > before[d.Id])
            .Select(d => d.Id)
            .ToHashSet();

        Assert.Equal(claimedByPolling, leased);
        Assert.Contains(dueNow.DeliveryId, leased);
        Assert.Contains(inProgressExpired.DeliveryId, leased);
        Assert.DoesNotContain(notDue.DeliveryId, leased);
        Assert.DoesNotContain(inProgressValid.DeliveryId, leased);
        Assert.DoesNotContain(succeeded.DeliveryId, leased);
        Assert.DoesNotContain(dead.DeliveryId, leased);
    }
}
