using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Tests.Persistence;

/// <summary>
/// Starts one real PostgreSQL container for all persistence tests and applies the real
/// migrations to it. Requires Docker to be running.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public WebhookDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WebhookDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        return new WebhookDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
