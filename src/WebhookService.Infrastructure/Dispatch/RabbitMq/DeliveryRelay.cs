using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebhookService.Infrastructure.Dispatch.RabbitMq;

/// <param name="Leased">How many deliveries were found due and leased.</param>
/// <param name="Failed">How many of those could not be announced and had their lease given back.</param>
public readonly record struct RelayResult(int Leased, int Failed);

/// <summary>
/// Finds the deliveries that are due and announces them through an <see cref="IDeliveryPublisher"/> (ADR 0010).
/// The order matters: the lease is committed first, then the message is sent, and if sending fails the lease is given
/// back. A relay that dies in between leaves a lease that simply runs out, so a delivery is announced late at worst,
/// never lost.
/// </summary>
public sealed class DeliveryRelay
{
    private readonly DeliveryEnqueueStore _store;
    private readonly IDeliveryPublisher _publisher;
    private readonly DeliveryOptions _options;
    private readonly ILogger<DeliveryRelay> _logger;

    public DeliveryRelay(
        DeliveryEnqueueStore store,
        IDeliveryPublisher publisher,
        IOptions<DeliveryOptions> options,
        ILogger<DeliveryRelay> logger)
    {
        _store = store;
        _publisher = publisher;
        _options = options.Value;
        _options.EnsureValid();
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await RelayOnceAsync(ct);

                if (result.Failed > 0)
                {
                    // The broker is struggling: do not ask again at once.
                    await Task.Delay(_options.ErrorBackoff, ct);
                }
                else if (result.Leased == 0)
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
                _logger.LogError(ex, "Delivery relay pass failed. Retrying in {Delay}.", _options.ErrorBackoff);

                try
                {
                    await Task.Delay(_options.ErrorBackoff, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async Task<RelayResult> RelayOnceAsync(CancellationToken ct)
    {
        var ids = await _store.LeaseDueAsync(ct);
        if (ids.Count == 0)
        {
            return new RelayResult(0, 0);
        }

        IReadOnlyList<Guid> unconfirmed;
        try
        {
            unconfirmed = await _publisher.PublishAsync(ids, ct);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                // Stopping: give the leases back so a restart does not have to wait for them to run out.
                await ReleaseAsync(ids);
                throw;
            }

            _logger.LogError(ex, "{Count} deliveries could not be announced. They will be tried again.", ids.Count);
            await ReleaseAsync(ids);
            return new RelayResult(ids.Count, ids.Count);
        }

        if (unconfirmed.Count > 0)
        {
            _logger.LogWarning(
                "The broker did not confirm {Count} of {Total} announcements. They will be tried again.",
                unconfirmed.Count,
                ids.Count);
            await ReleaseAsync(unconfirmed);
        }

        return new RelayResult(ids.Count, unconfirmed.Count);
    }

    // Not tied to the caller's token: when the caller is being stopped, this is exactly the time it must still run.
    private async Task ReleaseAsync(IReadOnlyCollection<Guid> ids)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _store.ReleaseAsync(ids, timeout.Token);
    }
}
