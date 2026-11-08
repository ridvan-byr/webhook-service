using WebhookService.Core.Domain;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// The worker must never contact a non-public address, whatever the URL or DNS says.
/// These tests turn AllowPrivateNetworks off (the production setting) and use a real local receiver
/// to prove that nothing arrives there.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SsrfProtectionTests
{
    private const string Generic = "Destination is not a public address and was not contacted.";

    private readonly PostgresFixture _postgres;

    public SsrfProtectionTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_private_ip_destination_is_not_contacted()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await DispatchHarness.CreateAsync(_postgres, security: o => o.AllowPrivateNetworks = false);
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok"); // http://127.0.0.1:port

        await h.Processor.ProcessBatchAsync(default);

        Assert.Empty(receiver.Requests);
        await AssertBlockedAsync(h, seeded);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/admin")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[fd00::1]/")]
    public async Task Well_known_internal_addresses_are_blocked_before_any_connection_is_attempted(string url)
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, security: o => o.AllowPrivateNetworks = false);
        var seeded = await h.SeedAsync(url);

        await h.Processor.ProcessBatchAsync(default);

        await AssertBlockedAsync(h, seeded);
    }

    [Fact]
    public async Task A_host_name_that_resolves_to_a_private_address_is_not_contacted()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var resolver = new FakeResolver(new() { ["evil.test"] = ["127.0.0.1"] });
        await using var h = await DispatchHarness.CreateAsync(
            _postgres, security: o => o.AllowPrivateNetworks = false, resolver: resolver);
        var seeded = await h.SeedAsync($"http://evil.test:{Port(receiver)}/ok");

        await h.Processor.ProcessBatchAsync(default);

        Assert.Empty(receiver.Requests);
        Assert.Contains("evil.test", resolver.Lookups);
        await AssertBlockedAsync(h, seeded);
    }

    [Fact]
    public async Task One_private_address_among_public_ones_blocks_the_whole_host()
    {
        await using var receiver = await TestReceiver.StartAsync();

        // A hostile name that mixes answers, hoping the client picks the private one.
        var resolver = new FakeResolver(new() { ["mixed.test"] = ["93.184.216.34", "127.0.0.1"] });
        await using var h = await DispatchHarness.CreateAsync(
            _postgres, security: o => o.AllowPrivateNetworks = false, resolver: resolver);
        var seeded = await h.SeedAsync($"http://mixed.test:{Port(receiver)}/ok");

        await h.Processor.ProcessBatchAsync(default);

        Assert.Empty(receiver.Requests);
        await AssertBlockedAsync(h, seeded);
    }

    [Fact]
    public async Task The_connection_goes_to_the_address_that_was_resolved_not_to_a_second_lookup()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var resolver = new FakeResolver(new() { ["friendly.test"] = ["127.0.0.1"] });
        await using var h = await DispatchHarness.CreateAsync(_postgres, resolver: resolver); // private networks allowed
        var seeded = await h.SeedAsync($"http://friendly.test:{Port(receiver)}/ok");

        await h.Processor.ProcessBatchAsync(default);

        var request = Assert.Single(receiver.Requests);
        Assert.Equal($"friendly.test:{Port(receiver)}", request.Headers["Host"]);
        Assert.Equal(DeliveryStatus.Succeeded, (await h.GetDeliveryAsync(seeded.DeliveryId)).Status);
        Assert.Single(resolver.Lookups); // resolved once for this connection, then used as is
    }

    [Fact]
    public async Task A_blocked_delivery_is_retried_like_any_failure_and_finally_dies()
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, security: o => o.AllowPrivateNetworks = false);
        var seeded = await h.SeedAsync("http://169.254.169.254/");

        for (var i = 0; i < 3; i++) // MaxAttempts is 3 in the harness
        {
            await h.Processor.ProcessBatchAsync(default);
            h.Clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(DeliveryStatus.Dead, (await h.GetDeliveryAsync(seeded.DeliveryId)).Status);
    }

    [Fact]
    public async Task An_allowed_private_network_setting_lets_a_local_receiver_work_for_development()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await DispatchHarness.CreateAsync(_postgres, security: o => o.AllowPrivateNetworks = true);
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");

        await h.Processor.ProcessBatchAsync(default);

        Assert.Single(receiver.Requests);
        Assert.Equal(DeliveryStatus.Succeeded, (await h.GetDeliveryAsync(seeded.DeliveryId)).Status);
    }

    private static async Task AssertBlockedAsync(DispatchHarness h, Seeded seeded)
    {
        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);

        var attempt = Assert.Single(await h.GetAttemptsAsync(seeded.DeliveryId));
        Assert.Null(attempt.ResponseStatus);
        Assert.Equal(Generic, attempt.Error);

        // The text the tenant can read must not reveal where the name pointed.
        Assert.DoesNotContain("127.0.0.1", attempt.Error!);
        Assert.DoesNotContain("169.254", attempt.Error!);
        Assert.Equal(Generic, delivery.LastError);
    }

    private static int Port(TestReceiver receiver) => new Uri(receiver.BaseUrl).Port;
}
