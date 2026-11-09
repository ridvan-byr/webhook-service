using WebhookService.Infrastructure.Dispatch;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// The transport that needs nothing but the database: workers poll for due deliveries. It runs every test of
/// <see cref="DeliveryTransportContract"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresTransportTests : DeliveryTransportContract
{
    private readonly PostgresFixture _postgres;

    public PostgresTransportTests(PostgresFixture postgres) => _postgres = postgres;

    protected override Task<DispatchHarness> CreateHarnessAsync(Action<DeliveryOptions>? configure = null) =>
        DispatchHarness.CreateAsync(_postgres, configure);

    [Fact]
    public async Task A_delivery_that_another_worker_is_claiming_is_skipped_and_never_waited_for()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync();
        await h.SeedAsync($"{receiver.BaseUrl}/ok");

        // Another worker is in the middle of claiming it: its transaction holds the row's lock.
        await using var other = await h.LockDeliveriesAsync();
        var batch = h.Processor.ProcessBatchAsync(default);
        var finished = await Task.WhenAny(batch, Task.Delay(TimeSpan.FromSeconds(5)));

        try
        {
            // Waiting for the lock would make this time out: the claim would be stuck behind the other worker.
            Assert.Same(batch, finished);
            Assert.Equal(0, await batch);
            Assert.Empty(receiver.Requests);
        }
        finally
        {
            await other.ReleaseAsync(); // lets a claim that was stuck finish, so nothing is left hanging
            try
            {
                await batch;
            }
            catch
            {
                // Only the assertions above matter.
            }
        }

        // Once nobody is claiming it, it is delivered as usual.
        Assert.Equal(1, await h.Processor.ProcessBatchAsync(default));
    }
}
