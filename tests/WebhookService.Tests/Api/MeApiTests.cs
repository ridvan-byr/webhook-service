using System.Net;
using System.Text.Json;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

/// <summary>
/// GET /me tells the panel who is signed in. The tests read the JSON property names directly:
/// that is the contract the panel depends on.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MeApiTests : ApiTestBase
{
    public MeApiTests(PostgresFixture postgres)
        : base(postgres)
    {
    }

    [Fact]
    public async Task Me_returns_the_tenant_and_the_key_of_the_caller()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/me"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var me = json.RootElement;

        Assert.Equal(Tenant, me.GetProperty("tenantId").GetGuid());
        Assert.Equal("test tenant", me.GetProperty("tenantName").GetString());
        Assert.Equal("test", me.GetProperty("apiKeyName").GetString());
        Assert.NotEqual(Guid.Empty, me.GetProperty("apiKeyId").GetGuid());
    }

    [Fact]
    public async Task Each_caller_sees_only_their_own_tenant()
    {
        var other = Guid.NewGuid();

        var mine = await ReadMeAsync(Tenant);
        var theirs = await ReadMeAsync(other);

        Assert.Equal(Tenant, mine.GetProperty("tenantId").GetGuid());
        Assert.Equal(other, theirs.GetProperty("tenantId").GetGuid());
        Assert.NotEqual(mine.GetProperty("apiKeyId").GetGuid(), theirs.GetProperty("apiKeyId").GetGuid());
    }

    [Fact]
    public async Task Me_never_contains_the_key_itself_or_anything_derived_from_it()
    {
        var key = KeyFor(Tenant);

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/me"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(key, body);
        Assert.DoesNotContain("whk_", body);
        Assert.DoesNotContain("hash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prefix", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonElement> ReadMeAsync(Guid tenant)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/me", tenant: tenant));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
