using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WebhookService.Infrastructure.Persistence;

/// <summary>
/// Used only by the "dotnet ef" command line tool (for example when creating migrations).
/// It never runs in the application. The fallback matches the local docker-compose.yml.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WebhookDbContext>
{
    private const string LocalDevConnectionString =
        "Host=localhost;Port=5432;Database=webhooks;Username=webhooks;Password=webhooks_dev_password";

    public WebhookDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("WEBHOOKS_CONNECTION_STRING") ?? LocalDevConnectionString;

        var options = new DbContextOptionsBuilder<WebhookDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WebhookDbContext(options);
    }
}
