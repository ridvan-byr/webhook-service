using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookService.Infrastructure.Persistence;

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

        services.AddHttpClient<WebhookSender>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A receiver must not be able to redirect us to another address.
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

        services.AddSingleton<DeliveryProcessor>();

        return services;
    }
}
