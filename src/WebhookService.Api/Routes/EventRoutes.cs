using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Contracts;
using WebhookService.Api.Http;
using WebhookService.Infrastructure.Persistence;
using WebhookService.Infrastructure.Services;

namespace WebhookService.Api.Routes;

internal static class EventRoutes
{
    private const int MaxTypeLength = 200;
    private const int MaxKeyLength = 200;
    private const int MaxPayloadChars = 256_000;

    public static void MapEventRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/events", CreateAsync).WithTags("Events").WithName("CreateEvent");
        app.MapGet("/events/{id:guid}", GetAsync).WithTags("Events").WithName("GetEvent");
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpRequest request,
        WebhookDbContext db,
        CancellationToken ct)
    {
        if (!TenantHeader.TryRead(request, out var tenantId))
        {
            return Results.Problem(
                title: "Missing or invalid tenant.",
                detail: $"Send a valid GUID in the {TenantHeader.Name} header.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var ev = await db.Events.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (ev is null)
        {
            return Results.NotFound();
        }

        var deliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.EventId == id)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .ToListAsync(ct);

        using var payload = JsonDocument.Parse(ev.PayloadJson);

        return Results.Ok(new EventDetailResponse(
            ev.Id,
            ev.Type,
            ev.CreatedAt,
            payload.RootElement.Clone(),
            deliveries.Select(DeliveryRoutes.ToSummary).ToList()));
    }

    private static async Task<IResult> CreateAsync(
        HttpRequest request,
        CreateEventRequest body,
        EventIngestionService ingestion,
        CancellationToken ct)
    {
        if (!TenantHeader.TryRead(request, out var tenantId))
        {
            return Results.Problem(
                title: "Missing or invalid tenant.",
                detail: $"Send a valid GUID in the {TenantHeader.Name} header.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(body.Type))
        {
            errors["type"] = ["Is required."];
        }
        else if (body.Type.Trim().Length > MaxTypeLength)
        {
            errors["type"] = [$"Must be at most {MaxTypeLength} characters."];
        }

        // The Idempotency-Key header wins over the body field, so clients can use either.
        var headerKey = request.Headers["Idempotency-Key"].ToString();
        var key = string.IsNullOrWhiteSpace(headerKey) ? body.IdempotencyKey : headerKey;
        if (key is { Length: > MaxKeyLength })
        {
            errors["idempotencyKey"] = [$"Must be at most {MaxKeyLength} characters."];
        }

        string payload = string.Empty;
        if (body.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            errors["payload"] = ["Is required."];
        }
        else
        {
            // GetRawText returns the payload exactly as the sender wrote it (same spacing, same
            // key order). The signature is later computed over these exact characters.
            payload = body.Payload.GetRawText();
            if (payload.Length > MaxPayloadChars)
            {
                errors["payload"] = [$"Must be at most {MaxPayloadChars} characters."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await ingestion.IngestAsync(tenantId, body.Type!, payload, key, ct);

        return result.Outcome switch
        {
            IngestOutcome.Created => Results.Json(
                new EventAcceptedResponse(result.EventId, result.DeliveriesCreated, Duplicate: false),
                statusCode: StatusCodes.Status202Accepted),

            IngestOutcome.Duplicate => Results.Ok(
                new EventAcceptedResponse(result.EventId, result.DeliveriesCreated, Duplicate: true)),

            _ => Results.Problem(
                title: "Idempotency key already used.",
                detail: "This key was used for a different event. Use a new key for a new event.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }
}
