namespace WebhookService.Core.Domain;

/// <summary>
/// The stored side of an API key. It never contains the key itself, only the lookup prefix
/// and a hash of the full key.
/// </summary>
public sealed class ApiKey
{
    private ApiKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>A label chosen by the owner, for example "production" or "ci".</summary>
    public string Name { get; private set; } = string.Empty;

    public string Prefix { get; private set; } = string.Empty;

    public byte[] KeyHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public static ApiKey Create(Guid tenantId, string name, string prefix, byte[] keyHash, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Key name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("Prefix is required.", nameof(prefix));
        }

        if (keyHash is not { Length: 32 })
        {
            throw new ArgumentException("A SHA-256 hash (32 bytes) is required.", nameof(keyHash));
        }

        return new ApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            Prefix = prefix,
            KeyHash = keyHash,
            CreatedAt = now,
        };
    }

    /// <summary>Revoking twice keeps the first revocation time.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
