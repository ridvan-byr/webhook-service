using Microsoft.Extensions.DependencyInjection;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>A delivery harness whose transport is RabbitMQ: a real broker, a real relay and a real consumer.</summary>
public static class RabbitMqTestHost
{
    public static Task<DispatchHarness> CreateAsync(
        PostgresFixture postgres,
        RabbitMqOptions broker,
        Action<DeliveryOptions>? configure = null) =>
        DispatchHarness.CreateAsync(
            postgres,
            o =>
            {
                o.Transport = DeliveryTransport.RabbitMq;
                o.EnqueueLease = TimeSpan.FromMinutes(2);
                o.PollInterval = TimeSpan.FromMilliseconds(50);
                configure?.Invoke(o);
            },
            configureServices: services => AddBroker(services, broker));

    /// <summary>Points the harness's RabbitMQ settings at the test broker, with names of this test's own.</summary>
    public static void AddBroker(IServiceCollection services, RabbitMqOptions broker) =>
        services.Configure<RabbitMqOptions>(o =>
        {
            o.Host = broker.Host;
            o.Port = broker.Port;
            o.VirtualHost = broker.VirtualHost;
            o.Username = broker.Username;
            o.Password = broker.Password;
            o.Exchange = broker.Exchange;
            o.Queue = broker.Queue;
            o.PoisonQueue = broker.PoisonQueue;
            o.PublishTimeout = broker.PublishTimeout;
        });
}
