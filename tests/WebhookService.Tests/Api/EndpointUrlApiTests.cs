using System.Net;
using System.Net.Http.Json;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

/// <summary>Registration refuses addresses that point into a private network (the API runs with the production settings here).</summary>
[Collection(PostgresCollection.Name)]
public class EndpointUrlApiTests : ApiTestBase
{
    public EndpointUrlApiTests(PostgresFixture postgres)
        : base(postgres)
    {
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[::ffff:127.0.0.1]/hook")]
    [InlineData("https://2130706433/")]
    [InlineData("https://0x7f.0.0.1/")]
    [InlineData("https://metadata.google.internal/")]
    [InlineData("https://intranet/")]
    [InlineData("https://user:password@example.com/hook")]
    [InlineData("http://example.com/hook")] // plain http is refused by default
    public async Task Dangerous_or_insecure_urls_are_rejected_with_a_helpful_message(string url)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/endpoints", $"{{\"url\":\"{url}\"}}"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"url\"", body);
    }

    [Fact]
    public async Task A_rejected_url_is_not_stored()
    {
        await Client.SendAsync(Request(HttpMethod.Post, "/endpoints", "{\"url\":\"https://10.0.0.5/hook\"}"));

        var list = await Client.SendAsync(Request(HttpMethod.Get, "/endpoints"));
        var endpoints = await list.Content.ReadFromJsonAsync<List<WebhookService.Api.Contracts.EndpointResponse>>();

        Assert.Empty(endpoints!);
    }

    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("https://hooks.example.co.uk:8443/path?x=1")]
    [InlineData("https://8.8.8.8/hook")]
    public async Task Normal_public_https_urls_are_accepted(string url)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/endpoints", $"{{\"url\":\"{url}\"}}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
