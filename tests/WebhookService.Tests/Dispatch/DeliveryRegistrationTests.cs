using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookService.Infrastructure.Dispatch;

namespace WebhookService.Tests.Dispatch;

public class DeliveryRegistrationTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddWebhookDelivery(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("Delivery:BatchSize", "0")]
    [InlineData("Delivery:MaxParallelism", "0")]
    [InlineData("Delivery:ErrorBackoff", "00:00:00")]
    public void A_source_with_settings_that_make_no_sense_refuses_to_exist(string setting, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x",
                [setting] = value,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddWebhookDelivery(configuration);
        using var provider = services.BuildServiceProvider();

        // Resolved alone, without the processor, which checks the same settings: the source checks them itself.
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PostgresDeliverySource>());
    }

    [Fact]
    public void The_worker_gets_the_database_polling_source_by_default()
    {
        using var services = Build();

        Assert.IsType<PostgresDeliverySource>(services.GetRequiredService<IDeliverySource>());
    }

    [Fact]
    public void The_source_the_worker_gets_is_the_one_instance_the_tests_also_drive()
    {
        using var services = Build();

        Assert.Same(
            services.GetRequiredService<PostgresDeliverySource>(),
            services.GetRequiredService<IDeliverySource>());
    }
}
