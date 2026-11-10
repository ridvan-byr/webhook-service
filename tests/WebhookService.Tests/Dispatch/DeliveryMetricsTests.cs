using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// What the worker reports about itself. The numbers are what an operator would graph, so each one is checked against
/// something that really happened, and none of them may carry anything that identifies a customer or a delivery.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DeliveryMetricsTests
{
    private readonly PostgresFixture _postgres;

    public DeliveryMetricsTests(PostgresFixture postgres) => _postgres = postgres;

    private async Task<DispatchHarness> StartAsync(Action<DeliveryOptions>? configure = null) =>
        await DispatchHarness.CreateAsync(
            _postgres,
            o =>
            {
                o.Transport = DeliveryTransport.RabbitMq;
                configure?.Invoke(o);
            },
            configureServices: services =>
            {
                // Settings only: nothing here connects to a broker.
                RabbitMqTestHost.AddBroker(services, new RabbitMqOptions { Username = "u", Password = "p" });
                services.AddSingleton<IDeliveryPublisher, NullPublisher>();
            });

    private sealed class NullPublisher : IDeliveryPublisher
    {
        public Task<IReadOnlyList<Guid>> PublishAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private static MetricsProbe ProbeOf(DispatchHarness h) => new(h.Get<IMeterFactory>());

    [Fact]
    public async Task A_successful_attempt_is_counted_and_its_duration_recorded()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync();
        using var probe = ProbeOf(h);
        await h.SeedAsync($"{receiver.BaseUrl}/ok");

        await h.Processor.ProcessBatchAsync(default);

        Assert.Equal(1, probe.Sum("webhook.delivery.attempts", ("outcome", "succeeded")));
        Assert.Equal(0, probe.Sum("webhook.delivery.attempts", ("outcome", "failed")));
        var durations = probe.Values("webhook.delivery.attempt.duration");
        Assert.Single(durations);
        Assert.True(durations[0] >= 0);
    }

    [Fact]
    public async Task Failed_attempts_are_counted_as_failed_until_the_last_one_which_is_counted_as_dead()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync(); // MaxAttempts = 3
        using var probe = ProbeOf(h);
        await h.SeedAsync($"{receiver.BaseUrl}/fail");

        for (var i = 0; i < 3; i++)
        {
            await h.Processor.ProcessBatchAsync(default);
            h.Clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(2, probe.Sum("webhook.delivery.attempts", ("outcome", "failed")));
        Assert.Equal(1, probe.Sum("webhook.delivery.attempts", ("outcome", "dead")));
        Assert.Equal(0, probe.Sum("webhook.delivery.attempts", ("outcome", "succeeded")));
    }

    [Fact]
    public async Task An_attempt_whose_delivery_was_taken_over_meanwhile_is_counted_as_discarded_and_not_as_a_result()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync();
        using var probe = ProbeOf(h);
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");
        var claim = await h.Claims.TryClaimAsync(seeded.DeliveryId, default);

        // Another worker has the delivery by now (its attempt number is a different one).
        await h.Get<DeliveryProcessor>().DeliverAsync(claim! with { AttemptNumber = 99 }, default);

        Assert.Equal(1, probe.Sum("webhook.delivery.attempts", ("outcome", "discarded")));
        Assert.Equal(1, probe.Sum("webhook.delivery.attempts"));
        Assert.Empty(probe.Values("webhook.delivery.pickup.delay"));
    }

    [Fact]
    public async Task The_delay_between_a_delivery_being_due_and_its_attempt_starting_is_recorded()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync();
        using var probe = ProbeOf(h);
        await h.SeedAsync($"{receiver.BaseUrl}/ok");
        h.Clock.Advance(TimeSpan.FromSeconds(5)); // nobody came for it for five seconds

        await h.Processor.ProcessBatchAsync(default);

        Assert.Equal([5000d], probe.Values("webhook.delivery.pickup.delay"));
    }

    [Fact]
    public async Task A_delivery_that_is_attempted_late_after_a_retry_is_measured_from_when_the_retry_was_due()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync();
        await h.SeedAsync($"{receiver.BaseUrl}/fail");
        await h.Processor.ProcessBatchAsync(default); // first attempt fails, a retry is scheduled
        var retryDue = (await h.GetAllDeliveriesAsync()).Single().NextAttemptAt;
        using var probe = ProbeOf(h);
        h.Clock.Advance(retryDue - h.Clock.GetUtcNow() + TimeSpan.FromSeconds(2));

        await h.Processor.ProcessBatchAsync(default);

        // Not since the delivery was created, which would grow with every retry and say nothing about the worker.
        Assert.Equal([2000d], probe.Values("webhook.delivery.pickup.delay"));
    }

    [Fact]
    public async Task Metrics_carry_a_fixed_set_of_values_and_never_an_id_an_address_or_a_tenant()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await StartAsync();
        using var probe = ProbeOf(h);
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");
        await h.Processor.ProcessBatchAsync(default);

        var everything = probe.All.SelectMany(m => m.Tags).ToList();
        Assert.All(everything, tag => Assert.Equal("outcome", tag.Key));
        Assert.DoesNotContain(everything, tag => tag.Value?.ToString()?.Contains(seeded.DeliveryId.ToString()) == true);
        Assert.DoesNotContain(everything, tag => tag.Value?.ToString()?.Contains(receiver.BaseUrl) == true);
    }

    [Fact]
    public async Task Announcements_and_failed_announcements_are_counted()
    {
        await using var h = await DispatchHarness.CreateAsync(
            _postgres,
            o => o.Transport = DeliveryTransport.RabbitMq,
            configureServices: services =>
            {
                RabbitMqTestHost.AddBroker(services, new RabbitMqOptions { Username = "u", Password = "p" });
                services.AddSingleton<IDeliveryPublisher>(new RefusingPublisher());
            });
        using var probe = ProbeOf(h);
        await h.SeedAsync("http://unused.example/1");
        await h.SeedAsync("http://unused.example/2");

        await h.Relay.RelayOnceAsync(default);

        Assert.Equal(2, probe.Sum("webhook.relay.leased"));
        Assert.Equal(1, probe.Sum("webhook.relay.unconfirmed"));
    }

    [Fact]
    public async Task When_the_broker_cannot_be_used_at_all_every_announcement_counts_as_unconfirmed()
    {
        await using var h = await DispatchHarness.CreateAsync(
            _postgres,
            o => o.Transport = DeliveryTransport.RabbitMq,
            configureServices: services =>
            {
                RabbitMqTestHost.AddBroker(services, new RabbitMqOptions { Username = "u", Password = "p" });
                services.AddSingleton<IDeliveryPublisher>(new ThrowingPublisher());
            });
        using var probe = ProbeOf(h);
        await h.SeedAsync("http://unused.example/1");
        await h.SeedAsync("http://unused.example/2");

        await h.Relay.RelayOnceAsync(default);

        Assert.Equal(2, probe.Sum("webhook.relay.leased"));
        Assert.Equal(2, probe.Sum("webhook.relay.unconfirmed"));
    }

    private sealed class ThrowingPublisher : IDeliveryPublisher
    {
        public Task<IReadOnlyList<Guid>> PublishAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken ct) =>
            throw new InvalidOperationException("the broker is not there");
    }

    private sealed class RefusingPublisher : IDeliveryPublisher
    {
        public Task<IReadOnlyList<Guid>> PublishAsync(IReadOnlyList<Guid> deliveryIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([deliveryIds[0]]);
    }

    [Theory]
    [InlineData("handled")]
    [InlineData("ignored")]
    [InlineData("poison")]
    [InlineData("claim_failed")]
    public async Task What_became_of_each_message_is_counted_under_its_own_name(string outcome)
    {
        await using var h = await StartAsync();
        using var probe = ProbeOf(h);
        var seeded = await h.SeedAsync("http://unused.example/1");
        var source = h.Get<RabbitMqDeliverySource>();

        switch (outcome)
        {
            case "handled":
                await source.HandleMessageAsync(DeliveryMessage.Create(seeded.DeliveryId), (_, _) => Task.CompletedTask, default);
                break;
            case "ignored":
                await source.HandleMessageAsync(DeliveryMessage.Create(Guid.NewGuid()), (_, _) => Task.CompletedTask, default);
                break;
            case "poison":
                await source.HandleMessageAsync("not a delivery"u8.ToArray(), (_, _) => Task.CompletedTask, default);
                break;
            case "claim_failed":
                await h.ExecuteSqlAsync("ALTER TABLE deliveries RENAME TO deliveries_away");
                await source.HandleMessageAsync(DeliveryMessage.Create(seeded.DeliveryId), (_, _) => Task.CompletedTask, default);
                break;
        }

        Assert.Equal(1, probe.Sum("webhook.message.outcomes", ("outcome", outcome)));
        Assert.Equal(1, probe.Sum("webhook.message.outcomes"));
    }
}

/// <summary>Collects what one set of services measures, and nothing that other tests running at the same time do.</summary>
internal sealed class MetricsProbe : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private readonly List<Measurement> _all = [];

    public MetricsProbe(IMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "WebhookService" && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _listener.Start();
    }

    public record Measurement(string Name, double Value, Dictionary<string, object?> Tags);

    public IReadOnlyList<Measurement> All
    {
        get
        {
            lock (_gate)
            {
                return _all.ToList();
            }
        }
    }

    public double Sum(string name, params (string Key, string Value)[] tags) =>
        All.Where(m => m.Name == name && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
            .Sum(m => m.Value);

    public List<double> Values(string name) => All.Where(m => m.Name == name).Select(m => m.Value).ToList();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _all.Add(new Measurement(instrument.Name, value, copy));
        }
    }

    public void Dispose() => _listener.Dispose();
}
