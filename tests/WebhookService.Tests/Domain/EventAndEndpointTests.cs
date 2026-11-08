using WebhookService.Core.Domain;

namespace WebhookService.Tests.Domain;

public class EventAndEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Event_keeps_the_payload_exactly_as_given()
    {
        const string payload = "{ \"id\":  42 }";

        var ev = Event.Create(Guid.NewGuid(), " order.created ", payload, "  key-1 ", Now);

        Assert.Equal(payload, ev.PayloadJson);
        Assert.Equal("order.created", ev.Type);
        Assert.Equal("key-1", ev.IdempotencyKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_idempotency_key_means_no_key(string? key)
    {
        var ev = Event.Create(Guid.NewGuid(), "order.created", "{}", key, Now);

        Assert.Null(ev.IdempotencyKey);
    }

    [Theory]
    [InlineData("", "{}")]
    [InlineData("order.created", "")]
    [InlineData("  ", "{}")]
    public void Event_requires_a_type_and_a_payload(string type, string payload)
    {
        Assert.Throws<ArgumentException>(() => Event.Create(Guid.NewGuid(), type, payload, null, Now));
    }

    [Fact]
    public void Endpoint_starts_active()
    {
        var endpoint = Endpoint.Create(Guid.NewGuid(), new Uri("https://example.com/hook"), "secret", Now);

        Assert.True(endpoint.IsActive);
    }

    [Fact]
    public void Endpoint_can_be_deactivated_and_activated_again()
    {
        var endpoint = Endpoint.Create(Guid.NewGuid(), new Uri("https://example.com/hook"), "secret", Now);

        endpoint.Deactivate();
        Assert.False(endpoint.IsActive);

        endpoint.Activate();
        Assert.True(endpoint.IsActive);
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    public void Endpoint_only_accepts_http_and_https(string url)
    {
        Assert.Throws<ArgumentException>(
            () => Endpoint.Create(Guid.NewGuid(), new Uri(url), "secret", Now));
    }

    [Fact]
    public void Endpoint_requires_a_signing_secret()
    {
        Assert.Throws<ArgumentException>(
            () => Endpoint.Create(Guid.NewGuid(), new Uri("https://example.com/hook"), " ", Now));
    }

    [Fact]
    public void Attempt_is_a_success_only_for_2xx_responses()
    {
        var ok = DeliveryAttempt.Record(Guid.NewGuid(), Now, 120, 204, null, null);
        var serverError = DeliveryAttempt.Record(Guid.NewGuid(), Now, 120, 500, "oops", null);
        var timeout = DeliveryAttempt.Record(Guid.NewGuid(), Now, 30_000, null, null, "timeout");

        Assert.True(ok.IsSuccess);
        Assert.False(serverError.IsSuccess);
        Assert.False(timeout.IsSuccess);
    }

    [Fact]
    public void Attempt_shortens_a_long_response_body()
    {
        var attempt = DeliveryAttempt.Record(Guid.NewGuid(), Now, 10, 500, new string('x', 5_000), null);

        Assert.Equal(500, attempt.ResponseSnippet!.Length);
    }
}
