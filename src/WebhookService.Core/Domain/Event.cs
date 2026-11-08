namespace WebhookService.Core.Domain;

/// <summary>Something that happened in the sender's system and should be announced to subscribers.</summary>
public sealed class Event
{
    private Event()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>For example "order.created".</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>The body exactly as the sender provided it. The service never interprets it.</summary>
    public string PayloadJson { get; private set; } = string.Empty;

    /// <summary>Optional. A second event with the same key for the same tenant is ignored.</summary>
    public string? IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Event Create(
        Guid tenantId,
        string type,
        string payloadJson,
        string? idempotencyKey,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Event type is required.", nameof(type));
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("Payload is required.", nameof(payloadJson));
        }

        return new Event
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Type = type.Trim(),
            PayloadJson = payloadJson,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
            CreatedAt = now,
        };
    }
}
