using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebhookService.Tests.Api;

/// <summary>
/// In Development the API serves a Swagger document. It is built by Swashbuckle on top of the
/// OpenAPI libraries, so a package update that breaks that combination can stop the whole
/// application from starting. These tests start the real host in Development, without a database,
/// and read the document.
/// </summary>
public class SwaggerDocumentTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SwaggerDocumentTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            // Development settings would run migrations against the local database at startup.
            // This test only needs the document, so switch that off.
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:MigrateOnStartup"] = "false",
                }));
        });

    [Fact]
    public async Task The_swagger_document_is_generated_and_lists_the_routes()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var paths = document.RootElement.GetProperty("paths");

        foreach (var route in new[] { "/health", "/events", "/endpoints", "/deliveries", "/api-keys", "/admin/tenants" })
        {
            Assert.True(paths.TryGetProperty(route, out _), $"{route} is missing from the Swagger document");
        }
    }

    [Fact]
    public async Task The_application_starts_and_answers_in_development()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
