using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Contracts;
using WebhookService.Api.Security;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Api.Routes;

internal static class DeliveryRoutes
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static void MapDeliveryRoutes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/deliveries").WithTags("Deliveries");

        group.MapGet("/", ListAsync).WithName("ListDeliveries");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetDelivery");
        group.MapPost("/{id:guid}/resend", ResendAsync).WithName("ResendDelivery");
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        WebhookDbContext db,
        string? status,
        int? limit,
        CancellationToken ct)
    {
        var tenantId = user.TenantId();

        DeliveryStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Must be one of: {string.Join(", ", Enum.GetNames<DeliveryStatus>())}."],
                });
            }

            statusFilter = parsed;
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var query = OwnedDeliveries(db, tenantId).AsNoTracking();
        if (statusFilter is { } wanted)
        {
            query = query.Where(r => r.Delivery.Status == wanted);
        }

        var items = await query
            .OrderByDescending(r => r.Delivery.CreatedAt)
            .ThenBy(r => r.Delivery.Id)
            .Take(take)
            .ToListAsync(ct);

        return Results.Ok(items.Select(ToSummary));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ClaimsPrincipal user,
        WebhookDbContext db,
        CancellationToken ct)
    {
        var tenantId = user.TenantId();

        var row = await OwnedDeliveries(db, tenantId).AsNoTracking().SingleOrDefaultAsync(r => r.Delivery.Id == id, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        var attempts = await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.DeliveryId == id)
            .OrderBy(a => a.StartedAt)
            .ToListAsync(ct);

        return Results.Ok(new DeliveryDetailResponse(
            ToSummary(row),
            attempts.Select(a => new AttemptResponse(
                a.Id, a.StartedAt, a.DurationMs, a.ResponseStatus, a.ResponseSnippet, a.Error)).ToList()));
    }

    private static async Task<IResult> ResendAsync(
        Guid id,
        ClaimsPrincipal user,
        WebhookDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var tenantId = user.TenantId();

        var row = await OwnedDeliveries(db, tenantId).SingleOrDefaultAsync(r => r.Delivery.Id == id, ct);
        if (row is null)
        {
            return Results.NotFound();
        }

        try
        {
            row.Delivery.Requeue(time.GetUtcNow());
        }
        catch (InvalidDeliveryTransitionException)
        {
            return Results.Problem(
                title: "Only dead deliveries can be re-sent.",
                detail: $"This delivery is {row.Delivery.Status}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await db.SaveChangesAsync(ct);

        // 202: the delivery is queued again, the worker sends it shortly.
        return Results.Json(ToSummary(row), statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Deliveries have no tenant column of their own. They belong to the tenant of their event,
    /// so every query goes through the event. A delivery of another tenant looks like it does not exist.
    /// </summary>
    internal static IQueryable<DeliveryRow> OwnedDeliveries(WebhookDbContext db, Guid tenantId) =>
        from d in db.Deliveries
        join e in db.Events on d.EventId equals e.Id
        join ep in db.Endpoints on d.EndpointId equals ep.Id
        // The endpoint must belong to the same tenant too, so a delivery can never reveal the address of
        // another tenant's endpoint, even if the data were ever inconsistent.
        where e.TenantId == tenantId && ep.TenantId == tenantId
        select new DeliveryRow { Delivery = d, EventType = e.Type, EndpointUrl = ep.Url };

    internal static DeliverySummaryResponse ToSummary(DeliveryRow r) => new(
        r.Delivery.Id,
        r.Delivery.EventId,
        r.EventType,
        r.Delivery.EndpointId,
        r.EndpointUrl.ToString(),
        r.Delivery.Status.ToString(),
        r.Delivery.AttemptCount,
        r.Delivery.NextAttemptAt,
        r.Delivery.LastError,
        r.Delivery.CreatedAt);
}

/// <summary>
/// A delivery together with the readable facts about it that live on other rows. It uses properties
/// rather than constructor arguments because Entity Framework can then keep filtering and sorting
/// on top of the projection (a constructor call cannot be translated back into SQL).
/// </summary>
internal sealed class DeliveryRow
{
    public required Delivery Delivery { get; init; }

    public required string EventType { get; init; }

    public required Uri EndpointUrl { get; init; }
}
