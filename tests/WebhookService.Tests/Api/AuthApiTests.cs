using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Contracts;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

[Collection(PostgresCollection.Name)]
public class AuthApiTests : ApiTestBase
{
    public AuthApiTests(PostgresFixture postgres)
        : base(postgres)
    {
    }

    // ---------- routes are protected by default ----------

    [Theory]
    [InlineData("GET", "/endpoints")]
    [InlineData("POST", "/endpoints")]
    [InlineData("POST", "/events")]
    [InlineData("GET", "/deliveries")]
    [InlineData("GET", "/api-keys")]
    [InlineData("POST", "/api-keys")]
    public async Task Protected_routes_reject_requests_without_credentials(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task The_health_check_stays_open()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_old_tenant_header_no_longer_grants_access()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoints");
        request.Headers.Add("X-Tenant-Id", Tenant.ToString());

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Bearer not-a-key")]
    [InlineData("Bearer whk_000000000000_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task Wrong_credentials_are_rejected(string authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoints");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_with_the_right_prefix_but_a_wrong_secret_is_rejected()
    {
        var real = KeyFor(Tenant);
        var forged = real[..^3] + (real.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");

        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoints");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_scheme_name_is_case_insensitive()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoints");
        request.Headers.TryAddWithoutValidation("Authorization", "bearer " + KeyFor(Tenant));

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_key_only_sees_its_own_tenants_data()
    {
        await CreateEndpointAsync(Tenant);
        var other = Guid.NewGuid();

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/endpoints", tenant: other));
        var list = await response.Content.ReadFromJsonAsync<List<EndpointResponse>>();

        Assert.Empty(list!);
    }

    // ---------- creating tenants (operator) ----------

    [Fact]
    public async Task Tenant_creation_is_switched_off_when_no_admin_key_is_configured()
    {
        using var factory = new ApiFactory(Postgres.ConnectionString, adminKey: null);
        using var client = factory.CreateClient();

        var request = AdminRequest("Acme", AdminKey);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    [InlineData("test-admin-key ")]
    public async Task Tenant_creation_needs_the_right_admin_key(string? key)
    {
        var response = await Client.SendAsync(AdminRequest("Acme", key));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_tenant_api_key_is_not_an_admin_key()
    {
        var response = await Client.SendAsync(AdminRequest("Acme", KeyFor(Tenant)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Tenant_creation_needs_a_name(string name)
    {
        var response = await Client.SendAsync(AdminRequest(name, AdminKey));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_new_tenant_gets_a_working_key_and_starts_empty()
    {
        var response = await Client.SendAsync(AdminRequest("Acme", AdminKey));
        var created = await response.Content.ReadFromJsonAsync<TenantCreatedResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("whk_", created!.ApiKey);

        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoints");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.ApiKey);
        var list = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty((await list.Content.ReadFromJsonAsync<List<EndpointResponse>>())!);
    }

    [Fact]
    public async Task The_database_never_stores_the_key_itself()
    {
        var response = await Client.SendAsync(AdminRequest("Acme", AdminKey));
        var created = await response.Content.ReadFromJsonAsync<TenantCreatedResponse>();

        await using var db = Postgres.CreateContext();
        var stored = await db.ApiKeys.SingleAsync(k => k.Id == created!.ApiKeyId);

        Assert.Equal(32, stored.KeyHash.Length);
        Assert.NotEqual(Encoding.UTF8.GetBytes(created!.ApiKey), stored.KeyHash);
        Assert.Equal(12, stored.Prefix.Length);
        Assert.Contains(stored.Prefix, created.ApiKey);
    }

    // ---------- managing keys (tenant) ----------

    [Fact]
    public async Task A_tenant_can_create_list_and_use_additional_keys_without_ever_seeing_them_again()
    {
        var first = KeyFor(Tenant);
        var created = await CreateKeyAsync("ci");

        Assert.StartsWith("whk_", created.ApiKey);
        Assert.NotEqual(first, created.ApiKey);

        var listResponse = await Client.SendAsync(Request(HttpMethod.Get, "/api-keys"));
        var raw = await listResponse.Content.ReadAsStringAsync();
        var list = await listResponse.Content.ReadFromJsonAsync<List<ApiKeySummaryResponse>>();

        Assert.Equal(2, list!.Count);
        Assert.Contains(list, k => k.Name == "ci" && k.Prefix == created.Prefix);

        // Neither key appears in the listing, only the public prefixes.
        Assert.DoesNotContain(first, raw);
        Assert.DoesNotContain(created.ApiKey, raw);

        var useNewKey = new HttpRequestMessage(HttpMethod.Get, "/endpoints");
        useNewKey.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(useNewKey)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_stops_working_immediately()
    {
        var created = await CreateKeyAsync("temporary");

        var revoke = await Client.SendAsync(Request(HttpMethod.Delete, $"/api-keys/{created.Id}"));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoints");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(request)).StatusCode);

        var list = await (await Client.SendAsync(Request(HttpMethod.Get, "/api-keys")))
            .Content.ReadFromJsonAsync<List<ApiKeySummaryResponse>>();
        Assert.NotNull(list!.Single(k => k.Id == created.Id).RevokedAt);
    }

    [Fact]
    public async Task The_key_used_for_the_request_cannot_be_revoked()
    {
        KeyFor(Tenant);
        var own = await (await Client.SendAsync(Request(HttpMethod.Get, "/api-keys")))
            .Content.ReadFromJsonAsync<List<ApiKeySummaryResponse>>();

        var response = await Client.SendAsync(Request(HttpMethod.Delete, $"/api-keys/{own![0].Id}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Request(HttpMethod.Get, "/endpoints"))).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_key_cannot_be_revoked_or_even_found()
    {
        var other = Guid.NewGuid();
        var otherKey = await (await Client.SendAsync(Request(HttpMethod.Get, "/api-keys", tenant: other)))
            .Content.ReadFromJsonAsync<List<ApiKeySummaryResponse>>();

        var response = await Client.SendAsync(Request(HttpMethod.Delete, $"/api-keys/{otherKey![0].Id}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And it still works for its owner.
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Request(HttpMethod.Get, "/endpoints", tenant: other))).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_new_key_needs_a_name(string name)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/api-keys", $"{{\"name\":\"{name}\"}}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpRequestMessage AdminRequest(string name, string? adminKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/tenants")
        {
            Content = new StringContent($"{{\"name\":\"{name}\"}}", Encoding.UTF8, "application/json"),
        };

        if (adminKey is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Admin-Key", adminKey);
        }

        return request;
    }

    private async Task<ApiKeyCreatedResponse> CreateKeyAsync(string name)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/api-keys", $"{{\"name\":\"{name}\"}}"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiKeyCreatedResponse>())!;
    }
}
