namespace WebhookService.Core.Domain;

/// <summary>A URL that wants to receive events.</summary>
public sealed class Endpoint
{
    private Endpoint()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Uri Url { get; private set; } = null!;

    /// <summary>Used to sign every request so the receiver can verify where it came from.</summary>
    public string SigningSecret { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Endpoint Create(Guid tenantId, Uri url, string signingSecret, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Only http and https URLs are supported.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(signingSecret))
        {
            throw new ArgumentException("Signing secret is required.", nameof(signingSecret));
        }

        return new Endpoint
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Url = url,
            SigningSecret = signingSecret,
            IsActive = true,
            CreatedAt = now,
        };
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
