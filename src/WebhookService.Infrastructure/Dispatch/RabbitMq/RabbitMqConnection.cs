using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace WebhookService.Infrastructure.Dispatch.RabbitMq;

/// <summary>
/// The one connection to the broker that a process shares. It is opened when first needed (so a broker that is down
/// when the process starts does not stop it from starting) and the client library reconnects by itself when the
/// connection drops, re-declaring what was declared. The topology is declared once, right after the first connect.
/// </summary>
public sealed class RabbitMqConnection : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqConnection(IOptions<RabbitMqOptions> options)
    {
        _options = options.Value;
        _options.Validate();
    }

    public RabbitMqOptions Options => _options;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is null)
            {
                var connection = await CreateFactory().CreateConnectionAsync("webhook-service", ct);
                try
                {
                    await DeclareTopologyAsync(connection, ct);
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }

                _connection = connection;
            }

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ConnectionFactory CreateFactory() => new()
    {
        HostName = _options.Host,
        Port = _options.Port,
        VirtualHost = _options.VirtualHost,
        UserName = _options.Username,
        Password = _options.Password,
        Ssl = new SslOption { Enabled = _options.UseTls, ServerName = _options.Host },
        AutomaticRecoveryEnabled = true,
        TopologyRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
        RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Declares the exchange and the queues. Declaring is idempotent when the settings agree, so every process does
    /// it. If a queue exists with different settings the broker refuses, which is reported rather than overwritten.
    /// </summary>
    private async Task DeclareTopologyAsync(IConnection connection, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);

        // Messages that cannot be understood are rejected without being put back, and the broker moves them to the
        // poison queue through the default exchange, which routes by queue name.
        await channel.QueueDeclareAsync(
            _options.PoisonQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: ct);

        await channel.QueueDeclareAsync(
            _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = _options.PoisonQueue,
            },
            cancellationToken: ct);

        await channel.QueueBindAsync(_options.Queue, _options.Exchange, routingKey: _options.Queue, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync();
            }
            catch
            {
                // Closing a connection that is already gone is not a problem.
            }

            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
