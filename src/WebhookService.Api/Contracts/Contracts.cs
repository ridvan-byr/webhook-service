using System.Text.Json;

namespace WebhookService.Api.Contracts;

public sealed record CreateEndpointRequest(string? Url);

/// <summary>The signing secret is only included when the endpoint is created. It is never shown again.</summary>
public sealed record EndpointResponse(Guid Id, string Url, bool IsActive, DateTimeOffset CreatedAt);

public sealed record CreatedEndpointResponse(
    Guid Id,
    string Url,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string SigningSecret);

/// <param name="Payload">Any JSON value. Its original text is stored exactly as sent.</param>
public sealed record CreateEventRequest(string? Type, JsonElement Payload, string? IdempotencyKey);

public sealed record EventAcceptedResponse(Guid EventId, int Deliveries, bool Duplicate);
