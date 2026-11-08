using System.Net.Http.Json;
using System.Text;
using WebhookService.Api.Contracts;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

/// <summary>Shared helpers. Every test uses its own tenant, so tests never see each other's data.</summary>
public abstract class ApiTestBase : IDisposable
{
    protected ApiTestBase(PostgresFixture postgres)
    {
        Postgres = postgres;
        Factory = new ApiFactory(postgres.ConnectionString);
        Client = Factory.CreateClient();
        Tenant = Guid.NewGuid();
    }

    protected PostgresFixture Postgres { get; }

    protected ApiFactory Factory { get; }

    protected HttpClient Client { get; }

    protected Guid Tenant { get; }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    protected HttpRequestMessage Request(HttpMethod method, string url, string? json = null, Guid? tenant = null)
    {
        var message = new HttpRequestMessage(method, url);
        var tenantId = tenant ?? Tenant;
        if (tenantId != Guid.Empty)
        {
            message.Headers.Add("X-Tenant-Id", tenantId.ToString());
        }

        if (json is not null)
        {
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return message;
    }

    protected async Task<CreatedEndpointResponse> CreateEndpointAsync(Guid? tenant = null, string url = "https://example.com/hook")
    {
        var response = await Client.SendAsync(
            Request(HttpMethod.Post, "/endpoints", $"{{\"url\":\"{url}\"}}", tenant));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedEndpointResponse>())!;
    }

    protected Task<HttpResponseMessage> PostEventAsync(string json, string? idempotencyKeyHeader = null, Guid? tenant = null)
    {
        var message = Request(HttpMethod.Post, "/events", json, tenant);
        if (idempotencyKeyHeader is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKeyHeader);
        }

        return Client.SendAsync(message);
    }
}
