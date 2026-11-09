using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using WebhookService.Api.Contracts;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Services;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

/// <summary>
/// Shared helpers. Every test uses its own tenant with its own real API key, so tests never see
/// each other's data and every request goes through the real authentication.
/// </summary>
public abstract class ApiTestBase : IDisposable
{
    public const string AdminKey = "test-admin-key";

    private readonly Dictionary<Guid, string> _keys = new();

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

    public virtual void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    /// <summary>
    /// Builds a request signed in as <paramref name="tenant"/> (default: this test's tenant).
    /// Pass <see cref="Guid.Empty"/> for a request without any credentials.
    /// </summary>
    protected HttpRequestMessage Request(HttpMethod method, string url, string? json = null, Guid? tenant = null)
    {
        var message = new HttpRequestMessage(method, url);
        var tenantId = tenant ?? Tenant;
        if (tenantId != Guid.Empty)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", KeyFor(tenantId));
        }

        if (json is not null)
        {
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return message;
    }

    /// <summary>Creates the tenant and a real API key for it the first time it is needed.</summary>
    protected string KeyFor(Guid tenantId)
    {
        lock (_keys)
        {
            if (!_keys.TryGetValue(tenantId, out var key))
            {
                using var db = Postgres.CreateContext();
                db.Tenants.Add(Core.Domain.Tenant.Create(tenantId, "test tenant", DateTimeOffset.UtcNow));

                key = new ApiKeyService(db, TimeProvider.System)
                    .IssueKeyAsync(tenantId, "test", CancellationToken.None)
                    .GetAwaiter().GetResult().PlainText;

                _keys[tenantId] = key;
            }

            return key;
        }
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
