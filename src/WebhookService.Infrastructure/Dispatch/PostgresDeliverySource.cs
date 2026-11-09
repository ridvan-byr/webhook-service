using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebhookService.Infrastructure.Dispatch;

/// <summary>
/// The work source that needs nothing but the database: the worker polls for due deliveries and claims them
/// (see <see cref="DeliveryClaimStore"/> for what makes several workers safe).
/// </summary>
public sealed class PostgresDeliverySource : IDeliverySource
{
    private readonly DeliveryClaimStore _claims;
    private readonly DeliveryOptions _options;
    private readonly ILogger<PostgresDeliverySource> _logger;

    public PostgresDeliverySource(
        DeliveryClaimStore claims,
        IOptions<DeliveryOptions> options,
        ILogger<PostgresDeliverySource> logger)
    {
        _claims = claims;
        _options = options.Value;
        _options.EnsureValid();
        _logger = logger;
    }

    public async Task RunAsync(Func<DeliveryClaim, CancellationToken, Task> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(handle, ct);

                // Busy: ask for more work straight away. Idle: wait a little before asking again.
                if (processed == 0)
                {
                    await Task.Delay(_options.PollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // For example the database is briefly unreachable. Keep going.
                _logger.LogError(ex, "Delivery batch failed. Retrying in {Delay}.", _options.ErrorBackoff);

                try
                {
                    await Task.Delay(_options.ErrorBackoff, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Stopped while waiting to try again: that is a normal stop, not an error.
                    break;
                }
            }
        }
    }

    /// <summary>Claims one batch of due deliveries and hands each to <paramref name="handle"/>.</summary>
    /// <returns>How many deliveries were claimed. Zero means there is nothing to do right now.</returns>
    public async Task<int> ProcessBatchAsync(Func<DeliveryClaim, CancellationToken, Task> handle, CancellationToken ct)
    {
        var claimed = await _claims.ClaimDueBatchAsync(ct);
        if (claimed.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelism, CancellationToken = ct },
            async (claim, token) => await handle(claim, token));

        return claimed.Count;
    }
}
