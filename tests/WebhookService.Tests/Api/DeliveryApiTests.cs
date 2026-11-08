using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Contracts;
using WebhookService.Core.Domain;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

[Collection(PostgresCollection.Name)]
public class DeliveryApiTests : ApiTestBase
{
    private static readonly RetryPolicy Policy = new(MaxAttempts: 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public DeliveryApiTests(PostgresFixture postgres)
        : base(postgres)
    {
    }

    [Fact]
    public async Task An_event_can_be_read_back_with_its_payload_and_deliveries()
    {
        await CreateEndpointAsync();
        var eventId = await SendEventAsync("{\"type\":\"order.created\",\"payload\":{ \"id\": 42, \"items\": [1,2] }}");

        var response = await Client.SendAsync(Request(HttpMethod.Get, $"/events/{eventId}"));
        var detail = await response.Content.ReadFromJsonAsync<EventDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("order.created", detail!.Type);
        Assert.Equal(42, detail.Payload.GetProperty("id").GetInt32());
        Assert.Equal(2, detail.Payload.GetProperty("items").GetArrayLength());

        var delivery = Assert.Single(detail.Deliveries);
        Assert.Equal("Pending", delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
    }

    [Fact]
    public async Task Another_tenants_event_looks_like_it_does_not_exist()
    {
        var other = Guid.NewGuid();
        await CreateEndpointAsync(other);
        var eventId = await SendEventAsync("{\"type\":\"t\",\"payload\":{}}", tenant: other);

        var response = await Client.SendAsync(Request(HttpMethod.Get, $"/events/{eventId}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Listing_shows_only_the_callers_deliveries_newest_first()
    {
        await CreateEndpointAsync();
        var first = await SendEventAsync("{\"type\":\"t\",\"payload\":{\"n\":1}}");
        await Task.Delay(20);
        var second = await SendEventAsync("{\"type\":\"t\",\"payload\":{\"n\":2}}");

        var other = Guid.NewGuid();
        await CreateEndpointAsync(other);
        await SendEventAsync("{\"type\":\"t\",\"payload\":{}}", tenant: other);

        var list = await ListAsync("/deliveries");

        Assert.Equal(2, list.Count);
        Assert.Equal(second, list[0].EventId);
        Assert.Equal(first, list[1].EventId);
    }

    [Fact]
    public async Task Listing_can_be_filtered_by_status()
    {
        await CreateEndpointAsync();
        var deadEvent = await SendEventAsync("{\"type\":\"t\",\"payload\":{\"n\":1}}");
        await SendEventAsync("{\"type\":\"t\",\"payload\":{\"n\":2}}");
        await MakeDeadAsync(await DeliveryIdOfAsync(deadEvent));

        var dead = await ListAsync("/deliveries?status=dead"); // case does not matter
        var pending = await ListAsync("/deliveries?status=Pending");

        Assert.Equal(deadEvent, Assert.Single(dead).EventId);
        Assert.Single(pending);
    }

    [Fact]
    public async Task The_delivery_list_shows_the_event_type_and_the_endpoint_address()
    {
        await CreateEndpointAsync(url: "https://hooks.example.com/orders");
        await SendEventAsync("{\"type\":\"order.created\",\"payload\":{\"id\":1}}");

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/deliveries"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.EnumerateArray());

        Assert.Equal("order.created", item.GetProperty("eventType").GetString());
        Assert.Equal("https://hooks.example.com/orders", item.GetProperty("endpointUrl").GetString());
    }

    [Fact]
    public async Task The_delivery_detail_carries_the_event_type_and_the_endpoint_address()
    {
        await CreateEndpointAsync(url: "https://hooks.example.com/orders");
        var deliveryId = await DeliveryIdOfAsync(await SendEventAsync("{\"type\":\"order.created\",\"payload\":{}}"));

        var response = await Client.SendAsync(Request(HttpMethod.Get, $"/deliveries/{deliveryId}"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var delivery = json.RootElement.GetProperty("delivery");

        Assert.Equal("order.created", delivery.GetProperty("eventType").GetString());
        Assert.Equal("https://hooks.example.com/orders", delivery.GetProperty("endpointUrl").GetString());
    }

    [Fact]
    public async Task The_resend_answer_carries_the_event_type_and_the_endpoint_address()
    {
        await CreateEndpointAsync(url: "https://hooks.example.com/orders");
        var deliveryId = await DeliveryIdOfAsync(await SendEventAsync("{\"type\":\"order.created\",\"payload\":{}}"));
        await MakeDeadAsync(deliveryId);

        var response = await Client.SendAsync(Request(HttpMethod.Post, $"/deliveries/{deliveryId}/resend"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("order.created", json.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("https://hooks.example.com/orders", json.RootElement.GetProperty("endpointUrl").GetString());
    }

    [Fact]
    public async Task The_event_detail_lists_its_deliveries_with_the_event_type_and_the_endpoint_address()
    {
        await CreateEndpointAsync(url: "https://hooks.example.com/orders");
        var eventId = await SendEventAsync("{\"type\":\"order.created\",\"payload\":{}}");

        var response = await Client.SendAsync(Request(HttpMethod.Get, $"/events/{eventId}"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var delivery = Assert.Single(json.RootElement.GetProperty("deliveries").EnumerateArray());

        Assert.Equal("order.created", delivery.GetProperty("eventType").GetString());
        Assert.Equal("https://hooks.example.com/orders", delivery.GetProperty("endpointUrl").GetString());
    }

    [Fact]
    public async Task A_delivery_that_points_at_another_tenants_endpoint_never_reveals_its_address()
    {
        // The API cannot create this situation, so the data is written directly. It checks that the
        // queries do not trust the data blindly: an endpoint address belongs to its own tenant only.
        var other = Guid.NewGuid();
        var foreign = await CreateEndpointAsync(other, "https://secret.other-tenant.example/hook");
        var eventId = await SendEventAsync("{\"type\":\"order.created\",\"payload\":{}}"); // this tenant has no endpoints

        await using (var db = Postgres.CreateContext())
        {
            db.Add(Delivery.Create(eventId, foreign.Id, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var list = await (await Client.SendAsync(Request(HttpMethod.Get, "/deliveries"))).Content.ReadAsStringAsync();
        var detail = await (await Client.SendAsync(Request(HttpMethod.Get, $"/events/{eventId}"))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("other-tenant", list);
        Assert.DoesNotContain("other-tenant", detail);
    }

    [Fact]
    public async Task An_unknown_status_filter_is_rejected()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/deliveries?status=banana"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_limit_is_respected_and_kept_within_bounds()
    {
        await CreateEndpointAsync();
        for (var i = 0; i < 3; i++)
        {
            await SendEventAsync($"{{\"type\":\"t\",\"payload\":{{\"n\":{i}}}}}");
        }

        Assert.Equal(2, (await ListAsync("/deliveries?limit=2")).Count);
        Assert.Single(await ListAsync("/deliveries?limit=0")); // clamped up to 1
        Assert.Equal(3, (await ListAsync("/deliveries?limit=100000")).Count); // clamped down to the maximum
    }

    [Fact]
    public async Task A_delivery_shows_its_attempts_in_order()
    {
        await CreateEndpointAsync();
        var eventId = await SendEventAsync("{\"type\":\"t\",\"payload\":{}}");
        var deliveryId = await DeliveryIdOfAsync(eventId);
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var db = Postgres.CreateContext())
        {
            // Saved out of order on purpose: the API must sort by time.
            db.Add(DeliveryAttempt.Record(deliveryId, start.AddMinutes(5), 300, null, null, "Timed out after 10 s."));
            db.Add(DeliveryAttempt.Record(deliveryId, start, 120, 500, "boom", null));
            await db.SaveChangesAsync();
        }

        var response = await Client.SendAsync(Request(HttpMethod.Get, $"/deliveries/{deliveryId}"));
        var detail = await response.Content.ReadFromJsonAsync<DeliveryDetailResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, detail!.Attempts.Count);
        Assert.Equal(500, detail.Attempts[0].ResponseStatus);
        Assert.Equal("boom", detail.Attempts[0].ResponseSnippet);
        Assert.Null(detail.Attempts[1].ResponseStatus);
        Assert.Equal("Timed out after 10 s.", detail.Attempts[1].Error);
    }

    [Fact]
    public async Task Another_tenants_delivery_looks_like_it_does_not_exist()
    {
        var other = Guid.NewGuid();
        await CreateEndpointAsync(other);
        var eventId = await SendEventAsync("{\"type\":\"t\",\"payload\":{}}", tenant: other);
        var deliveryId = await DeliveryIdOfAsync(eventId);

        var get = await Client.SendAsync(Request(HttpMethod.Get, $"/deliveries/{deliveryId}"));
        var resend = await Client.SendAsync(Request(HttpMethod.Post, $"/deliveries/{deliveryId}/resend"));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resend.StatusCode);
    }

    [Fact]
    public async Task A_dead_delivery_can_be_re_sent()
    {
        await CreateEndpointAsync();
        var eventId = await SendEventAsync("{\"type\":\"t\",\"payload\":{}}");
        var deliveryId = await DeliveryIdOfAsync(eventId);
        await MakeDeadAsync(deliveryId);

        var response = await Client.SendAsync(Request(HttpMethod.Post, $"/deliveries/{deliveryId}/resend"));
        var summary = await response.Content.ReadFromJsonAsync<DeliverySummaryResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Pending", summary!.Status);
        Assert.Equal(0, summary.AttemptCount);

        await using var db = Postgres.CreateContext();
        var stored = await db.Deliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Pending, stored.Status);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Succeeded")]
    public async Task Only_dead_deliveries_can_be_re_sent(string state)
    {
        await CreateEndpointAsync();
        var eventId = await SendEventAsync("{\"type\":\"t\",\"payload\":{}}");
        var deliveryId = await DeliveryIdOfAsync(eventId);
        if (state == "Succeeded")
        {
            await MakeSucceededAsync(deliveryId);
        }

        var response = await Client.SendAsync(Request(HttpMethod.Post, $"/deliveries/{deliveryId}/resend"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = Postgres.CreateContext();
        Assert.Equal(state, (await db.Deliveries.SingleAsync(d => d.Id == deliveryId)).Status.ToString());
    }

    [Fact]
    public async Task Unknown_ids_are_not_found_and_missing_credentials_are_rejected()
    {
        var unknown = await Client.SendAsync(Request(HttpMethod.Get, $"/deliveries/{Guid.NewGuid()}"));
        var noCredentials = await Client.SendAsync(Request(HttpMethod.Get, "/deliveries", tenant: Guid.Empty));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noCredentials.StatusCode);
    }

    private async Task<Guid> SendEventAsync(string json, Guid? tenant = null)
    {
        var response = await PostEventAsync(json, tenant: tenant);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventAcceptedResponse>())!.EventId;
    }

    private async Task<List<DeliverySummaryResponse>> ListAsync(string url)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, url));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<DeliverySummaryResponse>>())!;
    }

    private async Task<Guid> DeliveryIdOfAsync(Guid eventId)
    {
        await using var db = Postgres.CreateContext();
        return await db.Deliveries.Where(d => d.EventId == eventId).Select(d => d.Id).SingleAsync();
    }

    private async Task MakeDeadAsync(Guid deliveryId)
    {
        await using var db = Postgres.CreateContext();
        var delivery = await db.Deliveries.SingleAsync(d => d.Id == deliveryId);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < Policy.MaxAttempts; i++)
        {
            delivery.StartAttempt(now, TimeSpan.FromMinutes(1));
            delivery.MarkFailed(now, "down", Policy, jitter: 0);
            now = delivery.NextAttemptAt;
        }

        await db.SaveChangesAsync();
    }

    private async Task MakeSucceededAsync(Guid deliveryId)
    {
        await using var db = Postgres.CreateContext();
        var delivery = await db.Deliveries.SingleAsync(d => d.Id == deliveryId);
        delivery.StartAttempt(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        delivery.MarkSucceeded();
        await db.SaveChangesAsync();
    }
}
