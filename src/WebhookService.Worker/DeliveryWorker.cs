using Microsoft.Extensions.Options;
using WebhookService.Infrastructure.Dispatch;

namespace WebhookService.Worker;

/// <summary>Runs the delivery loop for the lifetime of the process.</summary>
public sealed class DeliveryWorker : BackgroundService
{
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(5);

    private readonly DeliveryProcessor _processor;
    private readonly DeliveryOptions _options;
    private readonly ILogger<DeliveryWorker> _logger;

    public DeliveryWorker(
        DeliveryProcessor processor,
        IOptions<DeliveryOptions> options,
        ILogger<DeliveryWorker> logger)
    {
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Delivery worker started (batch size {BatchSize}).", _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await _processor.ProcessBatchAsync(stoppingToken);

                // Busy: ask for more work straight away. Idle: wait a little before asking again.
                if (processed == 0)
                {
                    await Task.Delay(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // For example the database is briefly unreachable. Keep the worker alive.
                _logger.LogError(ex, "Delivery batch failed. Retrying in {Delay}.", ErrorBackoff);
                await Task.Delay(ErrorBackoff, stoppingToken);
            }
        }

        _logger.LogInformation("Delivery worker stopped.");
    }
}
