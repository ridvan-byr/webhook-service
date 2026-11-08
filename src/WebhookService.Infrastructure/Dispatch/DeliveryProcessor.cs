using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Infrastructure.Dispatch;

/// <summary>
/// Claims due deliveries, sends them, and records what happened.
///
/// Claiming is the part that makes several workers safe: rows are locked with
/// "FOR UPDATE SKIP LOCKED", so a row that another worker is claiming is skipped instead of
/// waited for, and no two workers ever receive the same delivery.
/// </summary>
public sealed class DeliveryProcessor
{
    private readonly IDbContextFactory<WebhookDbContext> _dbFactory;
    private readonly WebhookSender _sender;
    private readonly TimeProvider _time;
    private readonly IJitter _jitter;
    private readonly DeliveryOptions _options;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<DeliveryProcessor> _logger;

    public DeliveryProcessor(
        IDbContextFactory<WebhookDbContext> dbFactory,
        WebhookSender sender,
        TimeProvider time,
        IJitter jitter,
        IOptions<DeliveryOptions> options,
        ILogger<DeliveryProcessor> logger)
    {
        _dbFactory = dbFactory;
        _sender = sender;
        _time = time;
        _jitter = jitter;
        _options = options.Value;
        _options.EnsureValid();
        _retryPolicy = _options.ToRetryPolicy();
        _logger = logger;
    }

    /// <returns>How many deliveries were claimed. Zero means there is nothing to do right now.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);
        if (claimed.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelism, CancellationToken = ct },
            async (claim, token) => await DeliverAsync(claim, token));

        return claimed.Count;
    }

    private sealed record Claim(Guid DeliveryId, int AttemptNumber);

    private async Task<List<Claim>> ClaimAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = _time.GetUtcNow();

        // Due: waiting for its time, or claimed by a worker whose claim has expired (it probably crashed).
        var due = await db.Deliveries.FromSqlInterpolated($"""
            SELECT * FROM deliveries
            WHERE ("Status" = 'Pending' AND "NextAttemptAt" <= {now})
               OR ("Status" = 'InProgress' AND "LockedUntil" <= {now})
            ORDER BY "NextAttemptAt"
            LIMIT {_options.BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        // The state change itself is done by the domain model, so its rules are enforced here too.
        foreach (var delivery in due)
        {
            delivery.StartAttempt(now, _options.LockDuration);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return due.Select(d => new Claim(d.Id, d.AttemptCount)).ToList();
    }

    private async Task DeliverAsync(Claim claim, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var delivery = await db.Deliveries.SingleAsync(d => d.Id == claim.DeliveryId, ct);
        var ev = await db.Events.AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
        var endpoint = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, ct);

        var startedAt = _time.GetUtcNow();
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

        _logger.LogInformation(
            "Delivery {DeliveryId} attempt {Attempt}: {Outcome} ({Status})",
            delivery.Id,
            claim.AttemptNumber,
            delivery.Status,
            result.Status?.ToString() ?? result.Error);
    }
}
