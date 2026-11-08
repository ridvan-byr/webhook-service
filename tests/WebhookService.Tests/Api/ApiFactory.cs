using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebhookService.Tests.Api;

/// <summary>Runs the real API in memory, connected to the test PostgreSQL container.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string? _adminKey;

    /// <param name="adminKey">Null means no admin key is configured, so tenant creation is switched off.</param>
    public ApiFactory(string connectionString, string? adminKey = ApiTestBase.AdminKey)
    {
        _connectionString = connectionString;
        _adminKey = adminKey;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" keeps appsettings.Development.json (local database settings) out of the tests.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);

        if (_adminKey is not null)
        {
            builder.UseSetting("Admin:ApiKey", _adminKey);
        }
    }
}
