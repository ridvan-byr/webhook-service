using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookService.Infrastructure.Persistence;
using WebhookService.Infrastructure.Security;

namespace WebhookService.Infrastructure.Dispatch;

public static class DeliveryServiceCollectionExtensions
{
    /// <summary>Registers everything the delivery worker needs.</summary>
    public static IServiceCollection AddWebhookDelivery(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DeliveryOptions>(configuration.GetSection(DeliveryOptions.SectionName));

        // TryAdd: a test (or the host) that registered its own clock or jitter first keeps it.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJitter, RandomJitter>();

        services.AddDbContextFactory<WebhookDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>()
                .GetConnectionString("Default")
                ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

            options.UseNpgsql(connectionString);
        });

        services.Configure<NetworkSecurityOptions>(configuration.GetSection(NetworkSecurityOptions.SectionName));
        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<SafeConnector>();

        services.AddHttpClient<WebhookSender>()
            .ConfigurePrimaryHttpMessageHandler(serviceProvider => new SocketsHttpHandler
            {
                // A receiver must not be able to redirect us to another address.
                AllowAutoRedirect = false,

                // Never go through a proxy configured in the environment: the connection must go to the
                // address that SafeConnector checked, not to whatever proxy happens to be set.
                UseProxy = false,

                ConnectTimeout = TimeSpan.FromSeconds(5),

                // Every connection is opened by the connector, which refuses non-public addresses.
                ConnectCallback = serviceProvider.GetRequiredService<SafeConnector>().ConnectAsync,
            });

        services.AddSingleton<DeliveryProcessor>();

        // Where the work comes from. The polling source needs nothing but the database. It is registered as
        // itself as well, because tests drive it one batch at a time.
        // The database half of a transport that works with messages. Nothing uses it in production yet.
        services.AddSingleton<DeliveryEnqueueStore>();

        services.AddSingleton<PostgresDeliverySource>();
        services.AddSingleton<IDeliverySource>(serviceProvider => serviceProvider.GetRequiredService<PostgresDeliverySource>());

        return services;
    }
}
