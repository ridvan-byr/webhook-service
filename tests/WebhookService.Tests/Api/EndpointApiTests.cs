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
    public async Task An_endpoint_can_be_deactivated_and_activated_again()
    {
        var created = await CreateEndpointAsync();

        var off = await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{created.Id}", "{\"isActive\":false}"));

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.Content.ReadFromJsonAsync<EndpointResponse>())!.IsActive);

        var afterOff = await Client.SendAsync(Request(HttpMethod.Get, $"/endpoints/{created.Id}"));
        Assert.False((await afterOff.Content.ReadFromJsonAsync<EndpointResponse>())!.IsActive);

        var on = await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{created.Id}", "{\"isActive\":true}"));

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await on.Content.ReadFromJsonAsync<EndpointResponse>())!.IsActive);
    }

    [Fact]
    public async Task A_deactivated_endpoint_receives_no_new_events_and_a_reactivated_one_does_again()
    {
        var created = await CreateEndpointAsync();
        const string Event = "{\"type\":\"order.created\",\"payload\":{\"id\":1}}";

        await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{created.Id}", "{\"isActive\":false}"));
        var whileOff = await (await PostEventAsync(Event)).Content.ReadFromJsonAsync<EventAcceptedResponse>();

        await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{created.Id}", "{\"isActive\":true}"));
        var whileOn = await (await PostEventAsync(Event)).Content.ReadFromJsonAsync<EventAcceptedResponse>();

        Assert.Equal(0, whileOff!.Deliveries);
        Assert.Equal(1, whileOn!.Deliveries);
    }

    [Fact]
    public async Task Changing_an_endpoint_to_the_state_it_already_has_is_fine()
    {
        var created = await CreateEndpointAsync();

        var first = await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{created.Id}", "{\"isActive\":false}"));
        var second = await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{created.Id}", "{\"isActive\":false}"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_endpoint_cannot_be_changed()
    {
        var other = Guid.NewGuid();
        var theirs = await CreateEndpointAsync(other);

        var response = await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{theirs.Id}", "{\"isActive\":false}"));
        var stillActive = await (await Client.SendAsync(Request(HttpMethod.Get, $"/endpoints/{theirs.Id}", tenant: other)))
            .Content.ReadFromJsonAsync<EndpointResponse>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(stillActive!.IsActive);
    }

    [Fact]
    public async Task An_unknown_endpoint_is_not_found()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{Guid.NewGuid()}", "{\"isActive\":false}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isActive\":null}")]
    [InlineData("{\"isActive\":\"yes\"}")]
    [InlineData("{\"isActive\":1}")]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_change_needs_a_true_or_false_value(string body)
    {
        var created = await CreateEndpointAsync();

        var response = await Client.SendAsync(Request(HttpMethod.Patch, $"/endpoints/{created.Id}", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var unchanged = await (await Client.SendAsync(Request(HttpMethod.Get, $"/endpoints/{created.Id}")))
            .Content.ReadFromJsonAsync<EndpointResponse>();
        Assert.True(unchanged!.IsActive);
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
    public async Task A_request_without_credentials_is_rejected()
    {
        var response = await Client.SendAsync(
            Request(HttpMethod.Post, "/endpoints", "{\"url\":\"https://example.com\"}", tenant: Guid.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
