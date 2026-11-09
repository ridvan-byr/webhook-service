using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using WebhookService.Infrastructure.Dispatch.RabbitMq;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// Starts one real RabbitMQ broker for the tests of the RabbitMQ transport. Requires Docker. The credentials are
/// throwaway values of the container, which lives only as long as the test run.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4.1-alpine")
        .WithUsername("test-user")
        .WithPassword("test-password-" + Guid.NewGuid().ToString("N"))
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(5672);

    /// <summary>Options for a test of its own: the exchange and the queues have names nobody else uses.</summary>
    public RabbitMqOptions CreateOptions()
    {
        var uri = new Uri(_container.GetConnectionString());
        var credentials = uri.UserInfo.Split(':', 2);
        var suffix = Guid.NewGuid().ToString("N")[..12];

        return new RabbitMqOptions
        {
            Host = Host,
            Port = Port,
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = Uri.UnescapeDataString(credentials[1]),
            Exchange = $"t.{suffix}.deliveries",
            Queue = $"t.{suffix}.due",
            PoisonQueue = $"t.{suffix}.poison",
            PublishTimeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>A plain client for looking at the broker from outside the code under test.</summary>
    public async Task<IConnection> OpenRawConnectionAsync(RabbitMqOptions options)
    {
        var factory = new ConnectionFactory
        {
            HostName = options.Host,
            Port = options.Port,
            UserName = options.Username,
            Password = options.Password,
        };

        return await factory.CreateConnectionAsync();
    }

    /// <summary>
    /// Stops the broker application inside the container (connections are dropped, the port stays the same) and
    /// starts it again. Queues and persistent messages are kept, as in a restart.
    /// </summary>
    public Task StopBrokerAsync() => RunCtlAsync("stop_app");

    public Task StartBrokerAsync() => RunCtlAsync("start_app");

    /// <summary>Sets the broker's memory limit; "0" raises the alarm at once, which blocks publishers.</summary>
    public Task SetMemoryWatermarkAsync(string fraction) => RunCtlAsync("set_vm_memory_high_watermark", fraction);

    private async Task RunCtlAsync(string command, params string[] arguments)
    {
        var result = await _container.ExecAsync(["rabbitmqctl", command, .. arguments]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"rabbitmqctl {command} failed: {result.Stderr}");
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqCollection :
    ICollectionFixture<RabbitMqFixture>,
    ICollectionFixture<PostgresFixture>
{
    public const string Name = "rabbitmq";
}
