using System.Net;
using System.Net.Http.Json;
using WebhookService.Api.Contracts;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

[Collection(PostgresCollection.Name)]
public class EndpointApiTests : ApiTestBase
{
    public EndpointApiTests(PostgresFixture postgres)
        : base(postgres)
    {
    }

    [Fact]
    public async Task Creating_an_endpoint_returns_a_generated_signing_secret_once()
    {
        var created = await CreateEndpointAsync();

        Assert.StartsWith("whsec_", created.SigningSecret);
        Assert.True(created.IsActive);

        var fetched = await Client.SendAsync(Request(HttpMethod.Get, $"/endpoints/{created.Id}"));
        var body = await fetched.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.DoesNotContain("whsec_", body);
        Assert.DoesNotContain("signingSecret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Each_endpoint_gets_a_different_secret()
    {
        var first = await CreateEndpointAsync();
        var second = await CreateEndpointAsync();

        Assert.NotEqual(first.SigningSecret, second.SigningSecret);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public async Task Invalid_urls_are_rejected(string url)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/endpoints", $"{{\"url\":\"{url}\"}}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_tenant_header_is_rejected()
    {
        var response = await Client.SendAsync(
            Request(HttpMethod.Post, "/endpoints", "{\"url\":\"https://example.com\"}", tenant: Guid.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Listing_only_shows_the_callers_own_endpoints()
    {
        var mine = await CreateEndpointAsync();
        await CreateEndpointAsync(tenant: Guid.NewGuid());

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/endpoints"));
        var list = await response.Content.ReadFromJsonAsync<List<EndpointResponse>>();

        Assert.Equal(mine.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task Another_tenants_endpoint_looks_like_it_does_not_exist()
    {
        var other = await CreateEndpointAsync(tenant: Guid.NewGuid());

        var response = await Client.SendAsync(Request(HttpMethod.Get, $"/endpoints/{other.Id}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
