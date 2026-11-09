using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Infrastructure.Dispatch;

/// <summary>
/// Takes deliveries for this worker: marks them as being attempted, counts the attempt and sets the lock. Both ways of
/// learning about work end here, so a delivery can only be attempted when the same conditions hold for both:
/// a worker that polls takes a batch of whatever is due, and a worker that was told about one delivery by message
/// takes that one.
///
/// A delivery is due when it is waiting for its time, or when a worker claimed it and its claim has expired (it
/// probably crashed). Rows are locked with "FOR UPDATE SKIP LOCKED", so a row that another worker is claiming is
/// skipped instead of waited for, and no two workers ever receive the same delivery.
/// </summary>
public sealed class DeliveryClaimStore
{
    private readonly IDbContextFactory<WebhookDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly DeliveryOptions _options;

    public DeliveryClaimStore(
        IDbContextFactory<WebhookDbContext> dbFactory,
        TimeProvider time,
        IOptions<DeliveryOptions> options)
    {
        _dbFactory = dbFactory;
        _time = time;
        _options = options.Value;
        _options.EnsureValid();
    }

    /// <summary>Claims up to one batch of whatever is due, oldest first.</summary>
    public async Task<List<DeliveryClaim>> ClaimDueBatchAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = _time.GetUtcNow();

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

        return due.Select(d => new DeliveryClaim(d.Id, d.AttemptCount)).ToList();
    }

    /// <summary>
    /// Claims one delivery by its id, if it is due. Returns null when it is not: it does not exist, it is finished, it
    /// is not due yet, another worker is attempting it, or another transaction is changing it this very moment.
    /// None of these is an error, and in none of them is anything changed.
    /// </summary>
    public async Task<DeliveryClaim?> TryClaimAsync(Guid deliveryId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = _time.GetUtcNow();

        var delivery = await db.Deliveries.FromSqlInterpolated($"""
            SELECT * FROM deliveries
            WHERE "Id" = {deliveryId}
              AND (("Status" = 'Pending' AND "NextAttemptAt" <= {now})
                OR ("Status" = 'InProgress' AND "LockedUntil" <= {now}))
            FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);

        if (delivery is null)
        {
            return null;
        }

        delivery.StartAttempt(now, _options.LockDuration);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new DeliveryClaim(delivery.Id, delivery.AttemptCount);
    }
}
