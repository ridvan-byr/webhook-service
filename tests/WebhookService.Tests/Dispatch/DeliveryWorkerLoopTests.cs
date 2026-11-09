using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebhookService.Core.Domain;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// The loop that runs in the worker process: keep asking for work, rest when there is none, survive a failing
/// batch, stop cleanly. These tests run a real background service, so they wait on the real clock; the waits are
/// short and every wait has a generous limit.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DeliveryWorkerLoopTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _postgres;

    public DeliveryWorkerLoopTests(PostgresFixture postgres) => _postgres = postgres;

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow + Limit;
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<bool> AllSucceededAsync(DispatchHarness h, IEnumerable<Seeded> seeded)
    {
        foreach (var s in seeded)
        {
            if ((await h.GetDeliveryAsync(s.DeliveryId)).Status != DeliveryStatus.Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public async Task A_running_worker_delivers_what_is_due_without_being_asked_batch_by_batch()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await DispatchHarness.CreateAsync(_postgres, o => o.PollInterval = TimeSpan.FromMilliseconds(20));
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");
        using var worker = h.CreateWorker();

        await worker.StartAsync(default);
        var delivered = await WaitForAsync(async () => (await h.GetDeliveryAsync(seeded.DeliveryId)).Status == DeliveryStatus.Succeeded);
        await worker.StopAsync(default);

        Assert.True(delivered, "the worker did not deliver within the limit");
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task While_there_is_work_it_asks_again_at_once_and_does_not_rest_between_batches()
    {
        await using var receiver = await TestReceiver.StartAsync();
        // A rest of ten minutes would make this test fail by timing out if the worker rested after a busy batch.
        await using var h = await DispatchHarness.CreateAsync(_postgres, o =>
        {
            o.BatchSize = 5;
            o.PollInterval = TimeSpan.FromMinutes(10);
        });
        var seeded = new List<Seeded>();
        for (var i = 0; i < 25; i++)
        {
            seeded.Add(await h.SeedAsync($"{receiver.BaseUrl}/ok"));
        }

        using var worker = h.CreateWorker();
        await worker.StartAsync(default);
        var allDone = await WaitForAsync(() => AllSucceededAsync(h, seeded));
        await worker.StopAsync(default);

        Assert.True(allDone, "25 deliveries in batches of 5 were not all delivered: the worker rested between batches");
        Assert.Equal(25, receiver.Requests.Count);
    }

    [Fact]
    public async Task When_there_is_nothing_to_do_it_rests_and_then_notices_new_work()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await DispatchHarness.CreateAsync(_postgres, o => o.PollInterval = TimeSpan.FromMilliseconds(150));
        using var worker = h.CreateWorker();
        await worker.StartAsync(default);

        await Task.Delay(400); // several rests with nothing to do
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");
        var delivered = await WaitForAsync(async () => (await h.GetDeliveryAsync(seeded.DeliveryId)).Status == DeliveryStatus.Succeeded);
        await worker.StopAsync(default);

        Assert.True(delivered, "work that appeared while the worker was resting was never found");
    }

    [Fact]
    public async Task With_nothing_to_do_it_asks_about_once_per_rest_and_does_not_spin()
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, o => o.PollInterval = TimeSpan.FromMilliseconds(200));
        using var worker = h.CreateWorker();
        var before = h.Clock.Reads;

        await worker.StartAsync(default);
        await Task.Delay(1_000);
        await worker.StopAsync(default);

        // About five rounds fit in a second with a rest of 200 ms. Without the rest it would be thousands.
        var rounds = h.Clock.Reads - before;
        Assert.InRange(rounds, 2, 12);
    }

    [Fact]
    public async Task A_failing_batch_is_not_retried_in_a_tight_loop()
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, o =>
        {
            o.PollInterval = TimeSpan.FromMilliseconds(20);
            o.ErrorBackoff = TimeSpan.FromMilliseconds(250);
        });
        await h.ExecuteSqlAsync("ALTER TABLE deliveries RENAME TO deliveries_away");
        using var worker = h.CreateWorker();
        var before = h.Clock.Reads;

        await worker.StartAsync(default);
        await Task.Delay(1_000);
        await worker.StopAsync(default);
        await h.ExecuteSqlAsync("ALTER TABLE deliveries_away RENAME TO deliveries");

        // About four tries fit in a second with a pause of 250 ms. Without the pause it would be thousands.
        var tries = h.Clock.Reads - before;
        Assert.InRange(tries, 2, 10);
    }

    [Fact]
    public async Task A_failing_batch_does_not_stop_the_worker_which_carries_on_after_a_pause()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await DispatchHarness.CreateAsync(_postgres, o =>
        {
            o.PollInterval = TimeSpan.FromMilliseconds(20);
            o.ErrorBackoff = TimeSpan.FromMilliseconds(100);
        });
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");

        // The table is not there for a moment, as if the database were briefly unusable: claiming fails.
        await h.ExecuteSqlAsync("ALTER TABLE deliveries RENAME TO deliveries_away");
        using var worker = h.CreateWorker();
        await worker.StartAsync(default);
        await Task.Delay(400);
        Assert.Empty(receiver.Requests);

        await h.ExecuteSqlAsync("ALTER TABLE deliveries_away RENAME TO deliveries");
        var delivered = await WaitForAsync(async () => (await h.GetDeliveryAsync(seeded.DeliveryId)).Status == DeliveryStatus.Succeeded);
        await worker.StopAsync(default);

        Assert.True(delivered, "the worker did not recover after the failing batches");
    }

    [Fact]
    public async Task Stopping_during_a_rest_returns_promptly_and_without_an_error()
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, o => o.PollInterval = TimeSpan.FromHours(1));
        using var worker = h.CreateWorker();
        await worker.StartAsync(default);
        await Task.Delay(200); // let it reach its rest

        var stopping = worker.StopAsync(default);
        var finished = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(stopping, finished);
        await stopping; // would throw if stopping had failed
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully, $"the loop ended as {worker.ExecuteTask.Status}");
    }

    [Fact]
    public async Task Stopping_a_resting_worker_is_logged_as_a_stop_and_never_as_an_error()
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, o => o.PollInterval = TimeSpan.FromHours(1));
        using var worker = h.CreateWorker();
        await worker.StartAsync(default);
        await Task.Delay(200);

        await worker.StopAsync(default);

        Assert.DoesNotContain(h.Logs, line => line.Level >= LogLevel.Error);
        Assert.Contains(h.Logs, line => line.Message == "Delivery worker stopped.");
    }

    [Fact]
    public async Task Stopping_a_worker_that_is_pausing_after_an_error_logs_that_one_error_and_the_stop()
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, o =>
        {
            o.PollInterval = TimeSpan.FromMilliseconds(20);
            o.ErrorBackoff = TimeSpan.FromHours(1);
        });
        await h.ExecuteSqlAsync("ALTER TABLE deliveries RENAME TO deliveries_away");
        using var worker = h.CreateWorker();
        await worker.StartAsync(default);
        await Task.Delay(300);

        await worker.StopAsync(default);
        await h.ExecuteSqlAsync("ALTER TABLE deliveries_away RENAME TO deliveries");

        // The failure that caused the pause is the only error the service itself reports (the database library
        // reports its own as well). The stop itself is not one.
        var errors = h.Logs
            .Where(line => line.Level >= LogLevel.Error && line.Category.StartsWith("WebhookService"))
            .ToList();
        Assert.Single(errors);
        Assert.Contains("Delivery batch failed", errors[0].Message);
        Assert.Contains(h.Logs, line => line.Message == "Delivery worker stopped.");
    }

    [Fact]
    public async Task Stopping_during_a_pause_after_an_error_returns_promptly_too()
    {
        await using var h = await DispatchHarness.CreateAsync(_postgres, o =>
        {
            o.PollInterval = TimeSpan.FromMilliseconds(20);
            o.ErrorBackoff = TimeSpan.FromHours(1);
        });
        await h.ExecuteSqlAsync("ALTER TABLE deliveries RENAME TO deliveries_away");
        using var worker = h.CreateWorker();
        await worker.StartAsync(default);
        await Task.Delay(300); // the first batch has failed and the worker is pausing

        var stopping = worker.StopAsync(default);
        var finished = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(stopping, finished);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully, $"the loop ended as {worker.ExecuteTask.Status}");
        await h.ExecuteSqlAsync("ALTER TABLE deliveries_away RENAME TO deliveries");
    }
}
