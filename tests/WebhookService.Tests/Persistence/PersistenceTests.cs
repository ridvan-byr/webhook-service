using Microsoft.EntityFrameworkCore;
using WebhookService.Core.Domain;

namespace WebhookService.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public class PersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RetryPolicy Policy = RetryPolicy.Default;

    private readonly PostgresFixture _postgres;

    public PersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task An_event_and_its_delivery_survive_a_round_trip()
    {
        var tenant = Guid.NewGuid();
        var ev = Event.Create(tenant, "order.created", "{ \"id\":  42 }", null, Now);
        var endpoint = Endpoint.Create(tenant, new Uri("https://example.com/hook"), "secret", Now);
        var delivery = Delivery.Create(ev.Id, endpoint.Id, Now);

        await using (var write = _postgres.CreateContext())
        {
            write.AddRange(ev, endpoint, delivery);
            await write.SaveChangesAsync();
        }

        await using var read = _postgres.CreateContext();
        var savedEvent = await read.Events.SingleAsync(x => x.Id == ev.Id);
        var savedEndpoint = await read.Endpoints.SingleAsync(x => x.Id == endpoint.Id);
        var savedDelivery = await read.Deliveries.SingleAsync(x => x.Id == delivery.Id);

        // The payload must come back byte for byte, because the signature is computed over it.
        Assert.Equal("{ \"id\":  42 }", savedEvent.PayloadJson);
        Assert.Equal(new Uri("https://example.com/hook"), savedEndpoint.Url);
        Assert.Equal(DeliveryStatus.Pending, savedDelivery.Status);
        Assert.Equal(Now, savedDelivery.NextAttemptAt);
    }

    [Fact]
    public async Task Delivery_state_changes_are_persisted()
    {
        var delivery = await SaveNewDeliveryAsync();

        await using (var work = _postgres.CreateContext())
        {
            var loaded = await work.Deliveries.SingleAsync(x => x.Id == delivery.Id);
            loaded.StartAttempt(Now, TimeSpan.FromMinutes(2));
            loaded.MarkFailed(Now, "HTTP 500", Policy, jitter: 1);
            await work.SaveChangesAsync();
        }

        await using var read = _postgres.CreateContext();
        var saved = await read.Deliveries.SingleAsync(x => x.Id == delivery.Id);

        Assert.Equal(DeliveryStatus.Pending, saved.Status);
        Assert.Equal(1, saved.AttemptCount);
        Assert.Equal("HTTP 500", saved.LastError);
        Assert.True(saved.NextAttemptAt > Now);
    }

    [Fact]
    public async Task Status_is_stored_as_readable_text()
    {
        var delivery = await SaveNewDeliveryAsync();

        await using var db = _postgres.CreateContext();
        var raw = await db.Database
            .SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM deliveries WHERE \"Id\" = {delivery.Id}")
            .SingleAsync();

        Assert.Equal("Pending", raw);
    }

    [Fact]
    public async Task The_same_idempotency_key_cannot_be_used_twice_by_one_tenant()
    {
        var tenant = Guid.NewGuid();

        await using (var first = _postgres.CreateContext())
        {
            first.Add(Event.Create(tenant, "order.created", "{}", "order-1", Now));
            await first.SaveChangesAsync();
        }

        await using var second = _postgres.CreateContext();
        second.Add(Event.Create(tenant, "order.created", "{}", "order-1", Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Different_tenants_may_use_the_same_idempotency_key()
    {
        await using var db = _postgres.CreateContext();
        db.Add(Event.Create(Guid.NewGuid(), "order.created", "{}", "shared-key", Now));
        db.Add(Event.Create(Guid.NewGuid(), "order.created", "{}", "shared-key", Now));

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Events_without_a_key_are_never_treated_as_duplicates()
    {
        var tenant = Guid.NewGuid();

        await using var db = _postgres.CreateContext();
        db.Add(Event.Create(tenant, "order.created", "{}", null, Now));
        db.Add(Event.Create(tenant, "order.created", "{}", null, Now));

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_delivery_cannot_point_to_an_event_that_does_not_exist()
    {
        var tenant = Guid.NewGuid();
        var endpoint = Endpoint.Create(tenant, new Uri("https://example.com/hook"), "secret", Now);

        await using var db = _postgres.CreateContext();
        db.Add(endpoint);
        db.Add(Delivery.Create(Guid.NewGuid(), endpoint.Id, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Attempts_are_stored_and_removed_together_with_their_delivery()
    {
        var delivery = await SaveNewDeliveryAsync();
        var attempt = DeliveryAttempt.Record(delivery.Id, Now, 250, 503, "unavailable", null);

        await using (var write = _postgres.CreateContext())
        {
            write.Add(attempt);
            await write.SaveChangesAsync();
        }

        await using (var remove = _postgres.CreateContext())
        {
            await remove.Deliveries.Where(x => x.Id == delivery.Id).ExecuteDeleteAsync();
        }

        await using var read = _postgres.CreateContext();
        Assert.False(await read.DeliveryAttempts.AnyAsync(x => x.Id == attempt.Id));
    }

    private async Task<Delivery> SaveNewDeliveryAsync()
    {
        var tenant = Guid.NewGuid();
        var ev = Event.Create(tenant, "order.created", "{}", null, Now);
        var endpoint = Endpoint.Create(tenant, new Uri("https://example.com/hook"), "secret", Now);
        var delivery = Delivery.Create(ev.Id, endpoint.Id, Now);

        await using var db = _postgres.CreateContext();
        db.AddRange(ev, endpoint, delivery);
        await db.SaveChangesAsync();

        return delivery;
    }
}
