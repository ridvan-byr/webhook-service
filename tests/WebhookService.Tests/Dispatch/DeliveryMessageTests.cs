using System.Text;
using System.Text.Json;
using WebhookService.Infrastructure.Dispatch.RabbitMq;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// The message that announces a delivery: the id of the delivery and nothing else. The database holds the payload, the
/// address and the secret, so a message never has to, and the broker never sees them.
/// </summary>
public class DeliveryMessageTests
{
    private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void A_message_carries_the_id_and_comes_back_as_the_same_id()
    {
        var id = Guid.NewGuid();

        var parsed = DeliveryMessage.TryParse(DeliveryMessage.Create(id), out var read);

        Assert.True(parsed);
        Assert.Equal(id, read);
    }

    [Fact]
    public void A_message_contains_the_id_and_nothing_else()
    {
        var id = Guid.NewGuid();

        using var document = JsonDocument.Parse(DeliveryMessage.Create(id));

        var properties = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["deliveryId"], properties);
        Assert.Equal(id.ToString(), document.RootElement.GetProperty("deliveryId").GetString());
    }

    [Fact]
    public void A_message_is_small()
    {
        Assert.InRange(DeliveryMessage.Create(Guid.NewGuid()).Length, 1, 100);
    }

    [Fact]
    public void Properties_it_does_not_know_are_ignored_so_a_newer_sender_can_add_some()
    {
        var id = Guid.NewGuid();

        Assert.True(DeliveryMessage.TryParse(Body($"{{\"version\":2,\"deliveryId\":\"{id}\",\"hint\":[1,2]}}"), out var read));
        Assert.Equal(id, read);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    public void The_id_must_be_written_the_way_the_sender_writes_it_and_no_other_way(string format)
    {
        // A message is either exactly ours or it is not one. Being lenient about how an id is written would only
        // widen what a stray message can get through with.
        var id = Guid.NewGuid();

        Assert.False(DeliveryMessage.TryParse(Body($"{{\"deliveryId\":\"{id.ToString(format)}\"}}"), out _));
    }

    [Fact]
    public void The_id_written_the_usual_way_is_accepted_in_upper_and_lower_case()
    {
        var id = Guid.NewGuid();

        Assert.True(DeliveryMessage.TryParse(Body($"{{\"deliveryId\":\"{id.ToString().ToUpperInvariant()}\"}}"), out var read));
        Assert.Equal(id, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b\"")]
    [InlineData("{}")]
    [InlineData("{\"deliveryId\":null}")]
    [InlineData("{\"deliveryId\":123}")]
    [InlineData("{\"deliveryId\":\"nope\"}")]
    [InlineData("{\"deliveryId\":\"\"}")]
    [InlineData("{\"deliveryId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"deliveryId\":[\"3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b\"]}")]
    [InlineData("{\"DeliveryId\":\"3f2a91c0-1b2d-4e5f-8a9b-0c1d2e3f4a5b\"}")]
    public void Anything_that_is_not_such_a_message_is_refused_and_never_throws(string text)
    {
        Assert.False(DeliveryMessage.TryParse(Body(text), out var read));
        Assert.Equal(Guid.Empty, read);
    }

    [Fact]
    public void An_empty_body_is_refused()
    {
        Assert.False(DeliveryMessage.TryParse([], out _));
    }

    [Fact]
    public void A_body_that_is_far_too_large_to_be_such_a_message_is_refused_without_being_read()
    {
        var huge = Body("{\"deliveryId\":\"" + new string('a', 100_000) + "\"}");

        Assert.False(DeliveryMessage.TryParse(huge, out _));
    }

    [Fact]
    public void Random_bytes_are_refused_and_never_throw()
    {
        var random = new Random(12345);
        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);

            Assert.False(DeliveryMessage.TryParse(bytes, out _));
        }
    }

    [Fact]
    public void Deeply_nested_input_is_refused_and_never_throws()
    {
        var nested = new string('[', 5_000) + new string(']', 5_000);

        Assert.False(DeliveryMessage.TryParse(Body(nested), out _));
        Assert.False(DeliveryMessage.TryParse(Body("{\"deliveryId\":" + nested + "}"), out _));
    }

    [Fact]
    public void Invalid_utf8_is_refused_and_never_throws()
    {
        Assert.False(DeliveryMessage.TryParse([0xFF, 0xFE, 0x7B, 0x7D], out _));
    }
}
