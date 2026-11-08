using System.Security.Cryptography;
using System.Text;
using WebhookService.Core.Signing;

namespace WebhookService.Tests.Signing;

public class WebhookSignerTests
{
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{ \"id\":   42 }");

    [Fact]
    public void The_signature_matches_an_independent_hmac_computation()
    {
        var key = Convert.FromBase64String(Secret["whsec_".Length..]);
        var signedText = Encoding.UTF8.GetBytes("msg_1.1700000000.").Concat(Body).ToArray();
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signedText));

        var actual = WebhookSigner.Sign(Secret, "msg_1", 1_700_000_000, Body);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Signing_is_deterministic()
    {
        Assert.Equal(
            WebhookSigner.Sign(Secret, "msg_1", 100, Body),
            WebhookSigner.Sign(Secret, "msg_1", 100, Body));
    }

    [Fact]
    public void A_valid_signature_verifies()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Secret, "msg_1", timestamp, Body);

        Assert.True(WebhookSigner.Verify(Secret, "msg_1", timestamp.ToString(), signature, Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_changed_body_fails()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Secret, "msg_1", timestamp, Body);
        var tampered = Encoding.UTF8.GetBytes("{ \"id\":   43 }");

        Assert.False(WebhookSigner.Verify(Secret, "msg_1", timestamp.ToString(), signature, tampered, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_different_secret_fails()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Secret, "msg_1", timestamp, Body);
        var otherSecret = "whsec_" + Convert.ToBase64String(new byte[32]);

        Assert.False(WebhookSigner.Verify(otherSecret, "msg_1", timestamp.ToString(), signature, Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_different_message_id_fails()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Secret, "msg_1", timestamp, Body);

        Assert.False(WebhookSigner.Verify(Secret, "msg_2", timestamp.ToString(), signature, Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void An_old_request_is_rejected_even_with_a_valid_signature()
    {
        var oldTimestamp = Now.AddMinutes(-10).ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Secret, "msg_1", oldTimestamp, Body);

        Assert.False(WebhookSigner.Verify(Secret, "msg_1", oldTimestamp.ToString(), signature, Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_timestamp_far_in_the_future_is_rejected()
    {
        var futureTimestamp = Now.AddMinutes(10).ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Secret, "msg_1", futureTimestamp, Body);

        Assert.False(WebhookSigner.Verify(Secret, "msg_1", futureTimestamp.ToString(), signature, Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void During_secret_rotation_any_listed_signature_may_match()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var oldSecret = "whsec_" + Convert.ToBase64String(new byte[32]);
        var header = WebhookSigner.Sign(oldSecret, "msg_1", timestamp, Body) + " " + WebhookSigner.Sign(Secret, "msg_1", timestamp, Body);

        Assert.True(WebhookSigner.Verify(Secret, "msg_1", timestamp.ToString(), header, Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void A_malformed_timestamp_is_rejected(string timestamp)
    {
        var signature = WebhookSigner.Sign(Secret, "msg_1", Now.ToUnixTimeSeconds(), Body);

        Assert.False(WebhookSigner.Verify(Secret, "msg_1", timestamp, signature, Body, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_secret_without_the_prefix_is_used_as_plain_text()
    {
        var signature = WebhookSigner.Sign("plain-secret", "msg_1", 100, Body);

        Assert.StartsWith("v1,", signature);
    }

    [Fact]
    public void An_empty_secret_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => WebhookSigner.Sign("", "msg_1", 100, Body));
    }
}
