using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebhookService.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) =>
        // "Testing" means no database settings are loaded: /health must work without a database.
        _factory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    [Fact]
    public async Task Health_returns_ok_without_a_database()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
