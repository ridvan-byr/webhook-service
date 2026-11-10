using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Infrastructure.Dispatch;

/// <summary>
/// Attempts one delivery that this worker has claimed, and records what happened.
///
/// It does not care how the delivery reached the worker (see <see cref="IDeliverySource"/>): it sends the request,
/// checks that this attempt still owns the delivery, records the attempt and moves the delivery to its next state.
/// </summary>
public sealed class DeliveryProcessor
{
    private readonly IDbContextFactory<WebhookDbContext> _dbFactory;
    private readonly WebhookSender _sender;
    private readonly TimeProvider _time;
    private readonly IJitter _jitter;
    private readonly RetryPolicy _retryPolicy;
    private readonly DeliveryMetrics _metrics;
    private readonly ILogger<DeliveryProcessor> _logger;

    public DeliveryProcessor(
        IDbContextFactory<WebhookDbContext> dbFactory,
        WebhookSender sender,
        TimeProvider time,
        IJitter jitter,
        IOptions<DeliveryOptions> options,
        DeliveryMetrics metrics,
        ILogger<DeliveryProcessor> logger)
    {
        _dbFactory = dbFactory;
        _sender = sender;
        _time = time;
        _jitter = jitter;
        options.Value.EnsureValid();
        _retryPolicy = options.Value.ToRetryPolicy();
        _metrics = metrics;
        _logger = logger;
    }

    public async Task DeliverAsync(DeliveryClaim claim, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var delivery = await db.Deliveries.SingleAsync(d => d.Id == claim.DeliveryId, ct);
        var ev = await db.Events.AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
        var endpoint = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, ct);

        var startedAt = _time.GetUtcNow();
        // How long it has been due. A retry is measured from when the retry was due, not from when the delivery was made.
        var pickupDelayMs = (startedAt - delivery.NextAttemptAt).TotalMilliseconds;
        var result = endpoint.IsActive
            ? await _sender.SendAsync(endpoint.Url, endpoint.SigningSecret, delivery.EventId, ev.PayloadJson, ct)
            : new SendResult(null, null, "Endpoint is inactive.", 0);

        // The send may have taken long. Make sure this attempt still owns the delivery: if the
        // claim expired, another worker may have taken over and its result must win.
        await db.Entry(delivery).ReloadAsync(ct);
        if (delivery.Status != DeliveryStatus.InProgress || delivery.AttemptCount != claim.AttemptNumber)
        {
            _logger.LogWarning(
                "Delivery {DeliveryId} was taken over by another worker during attempt {Attempt}; result discarded.",
                claim.DeliveryId,
                claim.AttemptNumber);
            _metrics.AttemptDiscarded();
            return;
        }

        db.DeliveryAttempts.Add(DeliveryAttempt.Record(
            delivery.Id, startedAt, result.DurationMs, result.Status, result.ResponseSnippet, result.Error));

        if (result.IsSuccess)
        {
            delivery.MarkSucceeded();
        }
        else
        {
            var reason = result.Error ?? $"HTTP {result.Status}";
            delivery.MarkFailed(_time.GetUtcNow(), reason, _retryPolicy, _jitter.Next());
        }

        await db.SaveChangesAsync(ct);

        _metrics.AttemptFinished(
            result.IsSuccess ? DeliveryMetrics.Outcomes.Succeeded
                : delivery.Status == DeliveryStatus.Dead ? DeliveryMetrics.Outcomes.Dead
                : DeliveryMetrics.Outcomes.Failed,
            result.DurationMs,
            pickupDelayMs);

        _logger.LogInformation(
            "Delivery {DeliveryId} attempt {Attempt}: {Outcome} ({Status})",
            delivery.Id,
            claim.AttemptNumber,
            delivery.Status,
            result.Status?.ToString() ?? result.Error);
    }
}
