using System.Text.Json;

namespace WebhookService.Api.Contracts;

/// <param name="Status">Pending, InProgress, Succeeded or Dead.</param>
public sealed record DeliverySummaryResponse(
    Guid Id,
    Guid EventId,
    string EventType,
    Guid EndpointId,
    string EndpointUrl,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt);

public sealed record AttemptResponse(
    Guid Id,
    DateTimeOffset StartedAt,
    long DurationMs,
    int? ResponseStatus,
    string? ResponseSnippet,
    string? Error);

public sealed record DeliveryDetailResponse(
    DeliverySummaryResponse Delivery,
    IReadOnlyList<AttemptResponse> Attempts);

public sealed record EventDetailResponse(
    Guid Id,
    string Type,
    DateTimeOffset CreatedAt,
    JsonElement Payload,
    IReadOnlyList<DeliverySummaryResponse> Deliveries);
