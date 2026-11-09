using WebhookService.Infrastructure.Dispatch;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

[Collection(PostgresCollection.Name)]
public class DeliveryOptionsTests
{
    private readonly PostgresFixture _postgres;

    public DeliveryOptionsTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task A_lock_shorter_than_the_request_timeout_is_rejected()
    {
        // A lock shorter than the request timeout would let a second worker take over a slow request.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var h = await DispatchHarness.CreateAsync(_postgres, o =>
            {
                o.RequestTimeout = TimeSpan.FromSeconds(30);
                o.LockDuration = TimeSpan.FromSeconds(10);
            });
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_pause_after_an_error_that_is_not_positive_is_rejected(int seconds)
    {
        // Zero would turn a database that is down into a loop that asks as fast as it can.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var h = await DispatchHarness.CreateAsync(_postgres, o => o.ErrorBackoff = TimeSpan.FromSeconds(seconds));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_lease_that_is_not_positive_is_rejected(int seconds)
    {
        // Zero would let every delivery be announced again at once, on every pass.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var h = await DispatchHarness.CreateAsync(_postgres, o => o.EnqueueLease = TimeSpan.FromSeconds(seconds));
        });
    }

    [Fact]
    public void The_lease_is_two_minutes_unless_set()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), new DeliveryOptions().EnqueueLease);
    }

    [Fact]
    public void The_pause_after_an_error_is_five_seconds_unless_set()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new DeliveryOptions().ErrorBackoff);
    }
}
