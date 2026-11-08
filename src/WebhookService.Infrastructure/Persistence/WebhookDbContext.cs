using Microsoft.EntityFrameworkCore;
using WebhookService.Core.Domain;

namespace WebhookService.Infrastructure.Persistence;

/// <remarks>
/// All timestamps are DateTimeOffset values with a zero (UTC) offset. Npgsql refuses to
/// write any other offset into a "timestamptz" column, which catches local-time bugs early.
/// </remarks>
public sealed class WebhookDbContext : DbContext
{
    public WebhookDbContext(DbContextOptions<WebhookDbContext> options)
        : base(options)
    {
    }

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Endpoint> Endpoints => Set<Endpoint>();

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(12).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(32).IsRequired();
            e.Ignore(x => x.IsRevoked);

            // Every request looks the key up by its prefix, so the prefix must be unique and indexed.
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasIndex(x => x.TenantId);

            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Event>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(200).IsRequired();

            // "text", not "jsonb": jsonb rewrites whitespace and key order, but the request
            // signature is computed over the exact bytes the sender provided.
            e.Property(x => x.PayloadJson).HasColumnType("text").IsRequired();

            e.Property(x => x.IdempotencyKey).HasMaxLength(200);

            // A second event with the same key for the same tenant is rejected by the database
            // itself, so two concurrent requests cannot both succeed. PostgreSQL treats NULLs
            // as distinct, so events without a key are never blocked by this index.
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
        });

        modelBuilder.Entity<Endpoint>(e =>
        {
            e.ToTable("endpoints");
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).HasMaxLength(2048).IsRequired();
            e.Property(x => x.SigningSecret).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Delivery>(e =>
        {
            e.ToTable("deliveries");
            e.HasKey(x => x.Id);

            // Stored as text so the table stays readable ("Pending", "Dead") when debugging.
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.LastError).HasColumnType("text");

            // The worker constantly asks: "which deliveries are due?". This index serves that query.
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });

            e.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Endpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("delivery_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.ResponseSnippet).HasMaxLength(500);
            e.Property(x => x.Error).HasColumnType("text");
            e.Ignore(x => x.IsSuccess);
            e.HasIndex(x => x.DeliveryId);

            e.HasOne<Delivery>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
