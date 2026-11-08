using WebhookService.Core.Domain;
using WebhookService.Core.Security;

namespace WebhookService.Tests.Security;

public class ApiKeyGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_generated_key_has_the_expected_shape()
    {
        var key = ApiKeyGenerator.Generate();

        Assert.StartsWith("whk_", key.PlainText);
        Assert.Equal(12, key.Prefix.Length);
        Assert.StartsWith($"whk_{key.Prefix}_", key.PlainText);
        Assert.Equal(32, key.Hash.Length);
    }

    [Fact]
    public void Keys_are_unique()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => ApiKeyGenerator.Generate().PlainText).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void The_secret_part_is_long_enough_to_be_unguessable()
    {
        var key = ApiKeyGenerator.Generate();
        var secret = key.PlainText[("whk_".Length + 12 + 1)..];

        // 32 random bytes encode to 43 base64url characters.
        Assert.Equal(43, secret.Length);
    }

    [Fact]
    public void The_hash_matches_only_the_original_key()
    {
        var key = ApiKeyGenerator.Generate();
        var other = ApiKeyGenerator.Generate();

        Assert.True(ApiKeyGenerator.Matches(key.PlainText, key.Hash));
        Assert.False(ApiKeyGenerator.Matches(other.PlainText, key.Hash));
        Assert.False(ApiKeyGenerator.Matches(key.PlainText + "x", key.Hash));
    }

    [Fact]
    public void The_stored_hash_does_not_contain_the_key()
    {
        var key = ApiKeyGenerator.Generate();

        Assert.DoesNotContain(key.Prefix, Convert.ToHexString(key.Hash).ToLowerInvariant());
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(key.PlainText), key.Hash);
    }

    [Fact]
    public void The_prefix_can_be_read_back_from_a_key()
    {
        var key = ApiKeyGenerator.Generate();

        Assert.True(ApiKeyGenerator.TryGetPrefix(key.PlainText, out var prefix));
        Assert.Equal(key.Prefix, prefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("whk_")]
    [InlineData("not-a-key")]
    [InlineData("whk_abcdef012345")] // no separator and no secret
    [InlineData("whk_abcdef012345_")] // empty secret
    [InlineData("whk_ABCDEF012345_secretsecretsecret")] // upper case is not valid hex here
    [InlineData("whk_abcdef01234z_secretsecretsecret")] // not hex
    [InlineData("xxx_abcdef012345_secretsecretsecret")] // wrong start
    [InlineData("whk_abcdef012345-secretsecretsecret")] // wrong separator
    public void Text_that_is_not_shaped_like_a_key_is_rejected(string? text)
    {
        Assert.False(ApiKeyGenerator.TryGetPrefix(text, out _));
    }

    [Fact]
    public void Secrets_are_compared_by_value()
    {
        Assert.True(ApiKeyGenerator.SecretsEqual("same", "same"));
        Assert.False(ApiKeyGenerator.SecretsEqual("same", "other"));
        Assert.False(ApiKeyGenerator.SecretsEqual("same", ""));
        Assert.False(ApiKeyGenerator.SecretsEqual("same", "same "));
    }

    [Fact]
    public void An_api_key_row_keeps_the_hash_and_never_needs_the_key()
    {
        var generated = ApiKeyGenerator.Generate();

        var apiKey = ApiKey.Create(Guid.NewGuid(), " production ", generated.Prefix, generated.Hash, Now);

        Assert.Equal("production", apiKey.Name);
        Assert.False(apiKey.IsRevoked);
        Assert.Equal(generated.Hash, apiKey.KeyHash);
    }

    [Fact]
    public void Revoking_marks_the_key_and_keeps_the_first_revocation_time()
    {
        var generated = ApiKeyGenerator.Generate();
        var apiKey = ApiKey.Create(Guid.NewGuid(), "k", generated.Prefix, generated.Hash, Now);

        apiKey.Revoke(Now.AddHours(1));
        apiKey.Revoke(Now.AddHours(5));

        Assert.True(apiKey.IsRevoked);
        Assert.Equal(Now.AddHours(1), apiKey.RevokedAt);
    }

    [Fact]
    public void An_api_key_needs_a_name_a_prefix_and_a_sha256_hash()
    {
        var generated = ApiKeyGenerator.Generate();

        Assert.Throws<ArgumentException>(() => ApiKey.Create(Guid.NewGuid(), " ", generated.Prefix, generated.Hash, Now));
        Assert.Throws<ArgumentException>(() => ApiKey.Create(Guid.NewGuid(), "k", "", generated.Hash, Now));
        Assert.Throws<ArgumentException>(() => ApiKey.Create(Guid.NewGuid(), "k", generated.Prefix, new byte[10], Now));
    }

    [Fact]
    public void A_tenant_needs_a_name_and_a_real_id()
    {
        Assert.Throws<ArgumentException>(() => Tenant.Create(" ", Now));
        Assert.Throws<ArgumentException>(() => Tenant.Create(Guid.Empty, "Acme", Now));
        Assert.Equal("Acme", Tenant.Create(" Acme ", Now).Name);
    }
}
