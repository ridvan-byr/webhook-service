using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Infrastructure.Dispatch;

/// <summary>
/// The database half of a transport that announces deliveries with messages (see ADR 0010): it finds the
/// deliveries that a worker could claim right now and puts a lease on them, so that they are not announced again
/// and again, and gives a lease back when the announcement could not be sent.
///
/// It announces nothing itself. A delivery under a lease is still an ordinary due delivery: a worker that polls the
/// database can claim it, and a claim ends the lease.
///
/// The lease is taken and committed <b>before</b> the message is sent, never while it is being sent. A message
/// that arrives while the lease is still being written would find the row locked and be dropped by a consumer
/// that skips locked rows, which would delay the delivery for the whole lease.
/// </summary>
public sealed class DeliveryEnqueueStore
{
    private readonly IDbContextFactory<WebhookDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly DeliveryOptions _options;

    public DeliveryEnqueueStore(
        IDbContextFactory<WebhookDbContext> dbFactory,
        TimeProvider time,
        IOptions<DeliveryOptions> options)
    {
        _dbFactory = dbFactory;
        _time = time;
        _options = options.Value;
        _options.EnsureValid();
    }

    /// <summary>
    /// Puts a lease on up to one batch of due deliveries, oldest first, and returns their ids. A delivery is due
    /// in the same cases a polling worker claims it (waiting for its time, or abandoned by a worker whose lock has
    /// expired), and is skipped while another lease on it has not run out or while another relay is leasing it.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> LeaseDueAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = _time.GetUtcNow();

        // The same "due" condition as the claim of PostgresDeliverySource, plus "not under a lease". They are
        // written separately because they have to be raw SQL; a test checks that they agree.
        var due = await db.Deliveries.FromSqlInterpolated($"""
            SELECT * FROM deliveries
            WHERE (("Status" = 'Pending' AND "NextAttemptAt" <= {now})
                OR ("Status" = 'InProgress' AND "LockedUntil" <= {now}))
              AND ("EnqueuedUntil" IS NULL OR "EnqueuedUntil" <= {now})
            ORDER BY "NextAttemptAt"
            LIMIT {_options.BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        // The rules of what may be leased are the domain model's, here as everywhere.
        foreach (var delivery in due)
        {
            delivery.MarkEnqueued(now, _options.EnqueueLease);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return due.Select(d => d.Id).ToList();
    }

    /// <summary>
    /// Gives leases back because the announcement could not be sent, so the deliveries are due again at once.
    /// A delivery that a worker has claimed in the meantime has no lease any more and is left as it is.
    /// </summary>
    public async Task ReleaseAsync(IReadOnlyCollection<Guid> deliveryIds, CancellationToken ct)
    {
        if (deliveryIds.Count == 0)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var leased = await db.Deliveries
            .Where(d => deliveryIds.Contains(d.Id) && d.EnqueuedUntil != null)
            .ToListAsync(ct);

        foreach (var delivery in leased)
        {
            delivery.ReleaseEnqueue();
        }

        await db.SaveChangesAsync(ct);
    }
}
