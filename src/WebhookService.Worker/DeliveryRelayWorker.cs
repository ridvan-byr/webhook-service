using Microsoft.Extensions.Options;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;

namespace WebhookService.Worker;

/// <summary>
/// Runs the relay of the RabbitMQ transport (ADR 0010): it finds due deliveries and announces them to the broker.
/// With any other transport it does nothing and ends at once, so the host can always register it.
/// </summary>
public sealed class DeliveryRelayWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DeliveryOptions _options;
    private readonly ILogger<DeliveryRelayWorker> _logger;

    public DeliveryRelayWorker(
        IServiceProvider services,
        IOptions<DeliveryOptions> options,
        ILogger<DeliveryRelayWorker> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Transport != DeliveryTransport.RabbitMq)
        {
            return;
        }

        // Resolved here and not in the constructor, so that settings which only the RabbitMQ transport needs are
        // never checked when another transport is chosen.
        var relay = (DeliveryRelay)_services.GetService(typeof(DeliveryRelay))!;

        _logger.LogInformation("Delivery relay started (announcing to RabbitMQ).");
        await relay.RunAsync(stoppingToken);
        _logger.LogInformation("Delivery relay stopped.");
    }
}
