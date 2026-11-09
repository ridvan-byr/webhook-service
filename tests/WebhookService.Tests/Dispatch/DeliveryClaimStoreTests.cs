using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// Taking one delivery, by its id, the way a worker that was told about it by message does. It goes through the same
/// state machine and the same SQL conditions as a worker that polls, so a message can never make a worker attempt
/// something a polling worker would not.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DeliveryClaimStoreTests
{
    private readonly PostgresFixture _postgres;

    public DeliveryClaimStoreTests(PostgresFixture postgres) => _postgres = postgres;

    private Task<DispatchHarness> StartAsync(Action<DeliveryOptions>? configure = null) =>
        DispatchHarness.CreateAsync(_postgres, o =>
        {
            o.LockDuration = TimeSpan.FromMinutes(1);
            configure?.Invoke(o);
        });

    [Fact]
    public async Task A_due_delivery_is_claimed_and_the_attempt_is_counted()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");

        var claim = await h.Claims.TryClaimAsync(seeded.DeliveryId, default);

        Assert.Equal(new DeliveryClaim(seeded.DeliveryId, 1), claim);
        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.InProgress, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromMinutes(1), delivery.LockedUntil);
    }

    [Fact]
    public async Task A_delivery_that_does_not_exist_is_not_claimed()
    {
        await using var h = await StartAsync();

        Assert.Null(await h.Claims.TryClaimAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task A_delivery_that_is_not_due_yet_is_not_claimed_and_is_left_as_it_was()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.ModifyDeliveryAsync(seeded.DeliveryId, d =>
        {
            d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            d.MarkFailed(h.Clock.GetUtcNow(), "boom", new RetryPolicy(5, TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)), 1);
        });

        Assert.Null(await h.Claims.TryClaimAsync(seeded.DeliveryId, default));

        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
    }

    [Fact]
    public async Task A_delivery_that_another_worker_is_attempting_is_not_claimed()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.ModifyDeliveryAsync(seeded.DeliveryId, d => d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1)));

        // A message arriving twice, or for a slow but healthy attempt, must not start a second attempt.
        Assert.Null(await h.Claims.TryClaimAsync(seeded.DeliveryId, default));
        Assert.Equal(1, (await h.GetDeliveryAsync(seeded.DeliveryId)).AttemptCount);
    }

    [Fact]
    public async Task A_delivery_abandoned_by_a_crashed_worker_is_claimed_once_its_lock_has_expired()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.ModifyDeliveryAsync(seeded.DeliveryId, d => d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1)));
        h.Clock.Advance(TimeSpan.FromSeconds(61));

        var claim = await h.Claims.TryClaimAsync(seeded.DeliveryId, default);

        Assert.Equal(new DeliveryClaim(seeded.DeliveryId, 2), claim);
    }

    [Fact]
    public async Task A_delivery_that_is_finished_is_never_claimed()
    {
        await using var h = await StartAsync();
        var succeeded = await h.SeedAsync("http://unused.example/1");
        await h.ModifyDeliveryAsync(succeeded.DeliveryId, d =>
        {
            d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            d.MarkSucceeded();
        });
        var dead = await h.SeedAsync("http://unused.example/2");
        await h.ModifyDeliveryAsync(dead.DeliveryId, d =>
        {
            d.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            d.MarkFailed(h.Clock.GetUtcNow(), "boom", new RetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), 1);
        });
        h.Clock.Advance(TimeSpan.FromDays(1));

        Assert.Null(await h.Claims.TryClaimAsync(succeeded.DeliveryId, default));
        Assert.Null(await h.Claims.TryClaimAsync(dead.DeliveryId, default));
    }

    [Fact]
    public async Task A_delivery_under_a_lease_is_claimed_and_the_claim_ends_the_lease()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");
        await h.Enqueue.LeaseDueAsync(default);

        var claim = await h.Claims.TryClaimAsync(seeded.DeliveryId, default);

        Assert.NotNull(claim);
        Assert.Null((await h.GetDeliveryAsync(seeded.DeliveryId)).EnqueuedUntil);
    }

    [Fact]
    public async Task A_delivery_that_a_transaction_is_changing_right_now_is_skipped_and_never_waited_for()
    {
        await using var h = await StartAsync();
        var seeded = await h.SeedAsync("http://unused.example/ok");

        await using var other = await h.LockDeliveriesAsync();
        var claiming = h.Claims.TryClaimAsync(seeded.DeliveryId, default);
        var finished = await Task.WhenAny(claiming, Task.Delay(TimeSpan.FromSeconds(5)));

        try
        {
            Assert.Same(claiming, finished);
            Assert.Null(await claiming);
        }
        finally
        {
            await other.ReleaseAsync();
            try
            {
                await claiming;
            }
            catch
            {
                // Only the assertions above matter.
            }
        }
    }

    [Fact]
    public async Task When_the_same_delivery_is_claimed_by_many_at_once_exactly_one_gets_it()
    {
        await using var h = await StartAsync();

        for (var round = 0; round < 15; round++)
        {
            var seeded = await h.SeedAsync("http://unused.example/ok");

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Claims.TryClaimAsync(seeded.DeliveryId, default)));

            Assert.Single(results, claim => claim is not null);
            Assert.Equal(1, (await h.GetDeliveryAsync(seeded.DeliveryId)).AttemptCount);
        }
    }

    [Fact]
    public async Task A_batch_claims_what_is_due_up_to_the_batch_size_and_counts_each_attempt()
    {
        await using var h = await StartAsync(o => o.BatchSize = 2);
        var first = await h.SeedAsync("http://unused.example/1");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await h.SeedAsync("http://unused.example/2");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.SeedAsync("http://unused.example/3");

        var claims = await h.Claims.ClaimDueBatchAsync(default);

        Assert.Equal([new DeliveryClaim(first.DeliveryId, 1), new DeliveryClaim(second.DeliveryId, 1)], claims);
    }
}
