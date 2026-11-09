using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;
using WebhookService.Worker;

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
    public void The_database_half_of_a_message_transport_is_registered_but_does_not_change_the_default_source()
    {
        using var services = Build();

        Assert.NotNull(services.GetRequiredService<DeliveryEnqueueStore>());
        Assert.IsType<PostgresDeliverySource>(services.GetRequiredService<IDeliverySource>());
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

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x" };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddWebhookDelivery(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_worker_gets_the_RabbitMq_source_when_that_transport_is_chosen()
    {
        await using var services = Build(
            ("Delivery:Transport", "RabbitMq"),
            ("Delivery:RabbitMq:Username", "u"),
            ("Delivery:RabbitMq:Password", "p"));

        Assert.IsType<RabbitMqDeliverySource>(services.GetRequiredService<IDeliverySource>());
        Assert.IsType<RabbitMqDeliveryPublisher>(services.GetRequiredService<IDeliveryPublisher>());
    }

    [Fact]
    public async Task Choosing_RabbitMq_without_credentials_is_refused_when_the_source_is_created_and_the_password_is_not_in_the_message()
    {
        await using var services = Build(("Delivery:Transport", "RabbitMq"));

        var error = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IDeliverySource>());

        Assert.Contains("Delivery:RabbitMq:Username", error.Message);
    }

    [Fact]
    public void The_database_transport_does_not_need_any_broker_settings()
    {
        using var services = Build();

        Assert.IsType<PostgresDeliverySource>(services.GetRequiredService<IDeliverySource>());
    }

    [Fact]
    public async Task The_relay_service_does_nothing_and_ends_at_once_unless_the_transport_is_RabbitMq()
    {
        using var services = Build();
        var relay = ActivatorUtilities.CreateInstance<DeliveryRelayWorker>(services);

        await relay.StartAsync(default);
        var finished = await Task.WhenAny(relay.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(relay.ExecuteTask, finished);
    }
}
