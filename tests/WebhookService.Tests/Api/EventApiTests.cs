using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Contracts;
using WebhookService.Core.Domain;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

[Collection(PostgresCollection.Name)]
public class EventApiTests : ApiTestBase
{
    private const string SimpleEvent = "{\"type\":\"order.created\",\"payload\":{\"id\":42}}";

    public EventApiTests(PostgresFixture postgres)
        : base(postgres)
    {
    }

    [Fact]
    public async Task An_event_creates_one_delivery_per_active_endpoint()
    {
        await CreateEndpointAsync(url: "https://a.example.com/hook");
        await CreateEndpointAsync(url: "https://b.example.com/hook");

        var response = await PostEventAsync(SimpleEvent);
        var body = await response.Content.ReadFromJsonAsync<EventAcceptedResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, body!.Deliveries);
        Assert.False(body.Duplicate);
        Assert.Equal(2, await CountDeliveriesAsync(body.EventId));
    }

    [Fact]
    public async Task Inactive_endpoints_and_other_tenants_endpoints_get_no_delivery()
    {
        var inactive = await CreateEndpointAsync(url: "https://inactive.example.com/hook");
        await CreateEndpointAsync(tenant: Guid.NewGuid());
        await CreateEndpointAsync(url: "https://active.example.com/hook");

        await using (var db = Postgres.CreateContext())
        {
            var endpoint = await db.Endpoints.SingleAsync(e => e.Id == inactive.Id);
            endpoint.Deactivate();
            await db.SaveChangesAsync();
        }

        var response = await PostEventAsync(SimpleEvent);
        var body = await response.Content.ReadFromJsonAsync<EventAcceptedResponse>();

        Assert.Equal(1, body!.Deliveries);
    }

    [Fact]
    public async Task An_event_without_subscribers_is_still_accepted()
    {
        var response = await PostEventAsync(SimpleEvent);
        var body = await response.Content.ReadFromJsonAsync<EventAcceptedResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, body!.Deliveries);
    }

    [Fact]
    public async Task The_payload_is_stored_exactly_as_the_sender_wrote_it()
    {
        const string payload = "{ \"id\":   42,  \"b\": 1, \"a\": [1,  2] }";

        var response = await PostEventAsync($"{{\"type\":\"order.created\",\"payload\":{payload}}}");
        var body = await response.Content.ReadFromJsonAsync<EventAcceptedResponse>();

        await using var db = Postgres.CreateContext();
        var stored = await db.Events.SingleAsync(e => e.Id == body!.EventId);

        Assert.Equal(payload, stored.PayloadJson);
    }

    [Fact]
    public async Task Sending_the_same_event_with_the_same_key_twice_stores_it_once()
    {
        await CreateEndpointAsync();
        var json = "{\"type\":\"order.created\",\"payload\":{\"id\":1},\"idempotencyKey\":\"order-1\"}";

        var first = await PostEventAsync(json);
        var second = await PostEventAsync(json);

        var firstBody = await first.Content.ReadFromJsonAsync<EventAcceptedResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<EventAcceptedResponse>();

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(secondBody!.Duplicate);
        Assert.Equal(firstBody!.EventId, secondBody.EventId);
        Assert.Equal(1, await CountEventsAsync());
        Assert.Equal(1, await CountDeliveriesAsync(firstBody.EventId));
    }

    [Fact]
    public async Task The_idempotency_key_can_be_sent_as_a_header()
    {
        var json = "{\"type\":\"order.created\",\"payload\":{\"id\":1}}";

        var first = await PostEventAsync(json, idempotencyKeyHeader: "hdr-key");
        var second = await PostEventAsync(json, idempotencyKeyHeader: "hdr-key");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await CountEventsAsync());
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_event_is_a_conflict()
    {
        await PostEventAsync("{\"type\":\"order.created\",\"payload\":{\"id\":1}}", idempotencyKeyHeader: "k");

        var response = await PostEventAsync(
            "{\"type\":\"order.created\",\"payload\":{\"id\":2}}", idempotencyKeyHeader: "k");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await CountEventsAsync());
    }

    [Fact]
    public async Task The_same_key_may_be_used_by_different_tenants()
    {
        var json = "{\"type\":\"order.created\",\"payload\":{\"id\":1}}";

        var mine = await PostEventAsync(json, idempotencyKeyHeader: "shared");
        var theirs = await PostEventAsync(json, idempotencyKeyHeader: "shared", tenant: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Accepted, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, theirs.StatusCode);
    }

    [Fact]
    public async Task Ten_simultaneous_requests_with_the_same_key_store_exactly_one_event()
    {
        await CreateEndpointAsync();
        var json = "{\"type\":\"order.created\",\"payload\":{\"id\":7}}";

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => PostEventAsync(json, idempotencyKeyHeader: "race")));

        var bodies = new List<EventAcceptedResponse>();
        foreach (var response in responses)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
                $"Unexpected status {response.StatusCode}");
            bodies.Add((await response.Content.ReadFromJsonAsync<EventAcceptedResponse>())!);
        }

        Assert.Single(bodies.Select(b => b.EventId).Distinct());
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(1, await CountEventsAsync());
        Assert.Equal(1, await CountDeliveriesAsync(bodies[0].EventId));
    }

    [Theory]
    [InlineData("{\"payload\":{\"id\":1}}")]
    [InlineData("{\"type\":\"\",\"payload\":{\"id\":1}}")]
    [InlineData("{\"type\":\"order.created\"}")]
    [InlineData("{\"type\":\"order.created\",\"payload\":null}")]
    public async Task Events_without_a_type_or_payload_are_rejected(string json)
    {
        var response = await PostEventAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_request_without_credentials_is_rejected()
    {
        var response = await PostEventAsync(SimpleEvent, tenant: Guid.Empty);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_very_large_payload_is_rejected()
    {
        var big = new string('x', 300_000);

        var response = await PostEventAsync($"{{\"type\":\"order.created\",\"payload\":\"{big}\"}}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<int> CountEventsAsync()
    {
        await using var db = Postgres.CreateContext();
        return await db.Events.CountAsync(e => e.TenantId == Tenant);
    }

    private async Task<int> CountDeliveriesAsync(Guid eventId)
    {
        await using var db = Postgres.CreateContext();
        return await db.Deliveries.CountAsync(d => d.EventId == eventId);
    }
}
