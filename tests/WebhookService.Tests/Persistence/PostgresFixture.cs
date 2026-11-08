using Microsoft.EntityFrameworkCore;
using Npgsql;
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

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Creates a brand-new, migrated database inside the container. Tests that process every due
    /// delivery in the database (the worker tests) need their own, otherwise they would pick up
    /// rows that belong to other tests.
    /// </summary>
    public async Task<string> CreateIsolatedDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");

        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var isolated = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;

        var options = new DbContextOptionsBuilder<WebhookDbContext>().UseNpgsql(isolated).Options;
        await using var db = new WebhookDbContext(options);
        await db.Database.MigrateAsync();

        return isolated;
    }

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
