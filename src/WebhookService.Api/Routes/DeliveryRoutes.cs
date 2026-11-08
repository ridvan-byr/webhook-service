using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Contracts;
using WebhookService.Api.Http;
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
        HttpRequest request,
        WebhookDbContext db,
        string? status,
        int? limit,
        CancellationToken ct)
    {
        if (!TenantHeader.TryRead(request, out var tenantId))
        {
            return MissingTenant();
        }

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
            query = query.Where(d => d.Status == wanted);
        }

        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Take(take)
            .ToListAsync(ct);

        return Results.Ok(items.Select(ToSummary));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpRequest request,
        WebhookDbContext db,
        CancellationToken ct)
    {
        if (!TenantHeader.TryRead(request, out var tenantId))
        {
            return MissingTenant();
        }

        var delivery = await OwnedDeliveries(db, tenantId).AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null)
        {
            return Results.NotFound();
        }

        var attempts = await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.DeliveryId == id)
            .OrderBy(a => a.StartedAt)
            .ToListAsync(ct);

        return Results.Ok(new DeliveryDetailResponse(
            ToSummary(delivery),
            attempts.Select(a => new AttemptResponse(
                a.Id, a.StartedAt, a.DurationMs, a.ResponseStatus, a.ResponseSnippet, a.Error)).ToList()));
    }

    private static async Task<IResult> ResendAsync(
        Guid id,
        HttpRequest request,
        WebhookDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        if (!TenantHeader.TryRead(request, out var tenantId))
        {
            return MissingTenant();
        }

        var delivery = await OwnedDeliveries(db, tenantId).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null)
        {
            return Results.NotFound();
        }

        try
        {
            delivery.Requeue(time.GetUtcNow());
        }
        catch (InvalidDeliveryTransitionException)
        {
            return Results.Problem(
                title: "Only dead deliveries can be re-sent.",
                detail: $"This delivery is {delivery.Status}.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await db.SaveChangesAsync(ct);

        // 202: the delivery is queued again, the worker sends it shortly.
        return Results.Json(ToSummary(delivery), statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Deliveries have no tenant column of their own. They belong to the tenant of their event,
    /// so every query goes through the event. A delivery of another tenant looks like it does not exist.
    /// </summary>
    internal static IQueryable<Delivery> OwnedDeliveries(WebhookDbContext db, Guid tenantId) =>
        from d in db.Deliveries
        join e in db.Events on d.EventId equals e.Id
        where e.TenantId == tenantId
        select d;

    internal static DeliverySummaryResponse ToSummary(Delivery d) => new(
        d.Id, d.EventId, d.EndpointId, d.Status.ToString(), d.AttemptCount, d.NextAttemptAt, d.LastError, d.CreatedAt);

    private static IResult MissingTenant() =>
        Results.Problem(
            title: "Missing or invalid tenant.",
            detail: $"Send a valid GUID in the {TenantHeader.Name} header.",
            statusCode: StatusCodes.Status400BadRequest);
}
