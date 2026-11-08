namespace WebhookService.Core.Domain;

/// <summary>An immutable record of one HTTP try. This is the history you see in the dashboard.</summary>
public sealed class DeliveryAttempt
{
    private DeliveryAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid DeliveryId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public long DurationMs { get; private set; }

    /// <summary>Null when no response arrived (timeout, connection refused).</summary>
    public int? ResponseStatus { get; private set; }

    /// <summary>First part of the response body, kept short on purpose.</summary>
    public string? ResponseSnippet { get; private set; }

    public string? Error { get; private set; }

    public bool IsSuccess => ResponseStatus is >= 200 and < 300;

    public static DeliveryAttempt Record(
        Guid deliveryId,
        DateTimeOffset startedAt,
        long durationMs,
        int? responseStatus,
        string? responseSnippet,
        string? error) => new()
    {
        Id = Guid.NewGuid(),
        DeliveryId = deliveryId,
        StartedAt = startedAt,
        DurationMs = durationMs,
        ResponseStatus = responseStatus,
        ResponseSnippet = responseSnippet is { Length: > 500 } ? responseSnippet[..500] : responseSnippet,
        Error = error,
    };
}
