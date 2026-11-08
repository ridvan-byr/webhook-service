namespace WebhookService.Core.Domain;

/// <summary>A customer of the service. Events, endpoints and API keys belong to a tenant.</summary>
public sealed class Tenant
{
    private Tenant()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Tenant Create(string name, DateTimeOffset now) => Create(Guid.NewGuid(), name, now);

    public static Tenant Create(Guid id, string name, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id cannot be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name is required.", nameof(name));
        }

        return new Tenant { Id = id, Name = name.Trim(), CreatedAt = now };
    }
}
