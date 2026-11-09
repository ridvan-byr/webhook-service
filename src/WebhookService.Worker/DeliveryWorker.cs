using Microsoft.Extensions.Options;
using WebhookService.Infrastructure.Dispatch;

namespace WebhookService.Worker;

/// <summary>Runs the delivery loop for the lifetime of the process.</summary>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IDeliverySource _source;
    private readonly DeliveryProcessor _processor;
    private readonly DeliveryOptions _options;
    private readonly ILogger<DeliveryWorker> _logger;

    public DeliveryWorker(
        IDeliverySource source,
        DeliveryProcessor processor,
        IOptions<DeliveryOptions> options,
        ILogger<DeliveryWorker> logger)
    {
        _source = source;
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Delivery worker started (batch size {BatchSize}).", _options.BatchSize);

        // How deliveries reach this worker is the source's business; what happens to each one is the processor's.
        await _source.RunAsync(_processor.DeliverAsync, stoppingToken);

        _logger.LogInformation("Delivery worker stopped.");
    }
}
