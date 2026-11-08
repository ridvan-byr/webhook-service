using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Infrastructure.Services;

public enum IngestOutcome
{
    /// <summary>A new event was stored and delivery tasks were created.</summary>
    Created,

    /// <summary>The same idempotency key and the same content were already stored. Nothing changed.</summary>
    Duplicate,

    /// <summary>The idempotency key was already used for a different event.</summary>
    Conflict,
}

public sealed record IngestResult(IngestOutcome Outcome, Guid EventId, int DeliveriesCreated);

/// <summary>
/// Accepts an event from a sender. The event and one delivery task per active endpoint are
/// written in a single SaveChanges call, which is one database transaction: either the event
/// is accepted together with all its deliveries, or nothing is stored.
/// </summary>
public sealed class EventIngestionService
{
    private const string UniqueViolation = "23505";

    private readonly WebhookDbContext _db;
    private readonly TimeProvider _time;

    public EventIngestionService(WebhookDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<IngestResult> IngestAsync(
        Guid tenantId,
        string type,
        string payloadJson,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var ev = Event.Create(tenantId, type, payloadJson, idempotencyKey, now);

        // Fast path for the common retry case. It is only an optimization: the unique index
        // below is what actually guarantees there is never a second event with the same key.
        if (ev.IdempotencyKey is not null)
        {
            var existing = await FindByKeyAsync(tenantId, ev.IdempotencyKey, ct);
            if (existing is not null)
            {
                return await DescribeExistingAsync(existing, ev, ct);
            }
        }

        var endpointIds = await _db.Endpoints
            .Where(e => e.TenantId == tenantId && e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);

        _db.Events.Add(ev);
        foreach (var endpointId in endpointIds)
        {
            _db.Deliveries.Add(Delivery.Create(ev.Id, endpointId, now));
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            return new IngestResult(IngestOutcome.Created, ev.Id, endpointIds.Count);
        }
        catch (DbUpdateException ex) when (ev.IdempotencyKey is not null && IsUniqueViolation(ex))
        {
            // Two requests with the same key arrived at the same time and the other one won.
            // Forget our failed inserts and report what the winner stored.
            _db.ChangeTracker.Clear();

            var existing = await FindByKeyAsync(tenantId, ev.IdempotencyKey, ct)
                ?? throw new InvalidOperationException(
                    "A unique violation occurred but the existing event could not be found.", ex);

            return await DescribeExistingAsync(existing, ev, ct);
        }
    }

    private Task<Event?> FindByKeyAsync(Guid tenantId, string key, CancellationToken ct) =>
        _db.Events.AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key, ct);

    private async Task<IngestResult> DescribeExistingAsync(Event existing, Event incoming, CancellationToken ct)
    {
        var sameContent = existing.Type == incoming.Type && existing.PayloadJson == incoming.PayloadJson;
        if (!sameContent)
        {
            return new IngestResult(IngestOutcome.Conflict, existing.Id, 0);
        }

        var deliveries = await _db.Deliveries.CountAsync(d => d.EventId == existing.Id, ct);
        return new IngestResult(IngestOutcome.Duplicate, existing.Id, deliveries);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
