using System.Text;
using WebhookService.Core.Domain;
using WebhookService.Core.Signing;
using WebhookService.Infrastructure.Dispatch;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// What every way of carrying deliveries to a worker has to keep promising: the exact payload and a valid signature,
/// a retry that is not sent before its time, a dead delivery that is never sent again, receivers that are slow or
/// unreachable, a crashed worker's delivery picked up after its lock, and two workers never sending the same delivery.
///
/// A transport is tested by deriving from this class and saying how to start a harness for it
/// (<see cref="CreateHarnessAsync"/>); every test here then runs against it.
/// </summary>
public abstract class DeliveryTransportContract
{
    /// <summary>Starts a worker on its own empty database, using the transport under test.</summary>
    protected abstract Task<DispatchHarness> CreateHarnessAsync(Action<DeliveryOptions>? configure = null);

    [Fact]
    public async Task A_successful_delivery_sends_the_exact_payload_with_a_valid_signature()
    {
        const string payload = "{ \"id\":   42,  \"note\": \"çok güzel\" }";
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync();
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok", payload);

        var claimed = await h.Processor.ProcessBatchAsync(default);

        Assert.Equal(1, claimed);
        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);

        var request = Assert.Single(receiver.Requests);
        Assert.Equal(payload, request.Body);
        Assert.Equal(seeded.EventId.ToString(), request.Headers["Webhook-Id"]);
        Assert.True(WebhookSigner.Verify(
            seeded.Secret,
            request.Headers["Webhook-Id"],
            request.Headers["Webhook-Timestamp"],
            request.Headers["Webhook-Signature"],
            Encoding.UTF8.GetBytes(request.Body),
            h.Clock.GetUtcNow(),
            TimeSpan.FromMinutes(5)));

        var attempt = Assert.Single(await h.GetAttemptsAsync(seeded.DeliveryId));
        Assert.Equal(200, attempt.ResponseStatus);
        Assert.True(attempt.IsSuccess);
    }

    [Fact]
    public async Task A_failing_receiver_causes_a_recorded_attempt_and_a_scheduled_retry()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync();
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/fail");

        await h.Processor.ProcessBatchAsync(default);

        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal("HTTP 500", delivery.LastError);

        // Test jitter is 1, so the first retry waits the full base delay of 10 seconds.
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(10), delivery.NextAttemptAt);

        var attempt = Assert.Single(await h.GetAttemptsAsync(seeded.DeliveryId));
        Assert.Equal(500, attempt.ResponseStatus);
        Assert.Equal("boom", attempt.ResponseSnippet);
    }

    [Fact]
    public async Task A_retry_is_not_sent_before_its_time()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync();
        await h.SeedAsync($"{receiver.BaseUrl}/fail");

        await h.Processor.ProcessBatchAsync(default);
        var tooEarly = await h.Processor.ProcessBatchAsync(default);
        Assert.Equal(0, tooEarly);
        Assert.Single(receiver.Requests);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var onTime = await h.Processor.ProcessBatchAsync(default);

        Assert.Equal(1, onTime);
        Assert.Equal(2, receiver.Requests.Count);
    }

    [Fact]
    public async Task After_all_attempts_the_delivery_is_dead_and_never_sent_again()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync(); // MaxAttempts = 3
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/fail");

        for (var i = 0; i < 3; i++)
        {
            await h.Processor.ProcessBatchAsync(default);
            h.Clock.Advance(TimeSpan.FromHours(1));
        }

        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        Assert.Equal(3, delivery.AttemptCount);
        Assert.Equal(3, (await h.GetAttemptsAsync(seeded.DeliveryId)).Count);

        var again = await h.Processor.ProcessBatchAsync(default);
        Assert.Equal(0, again);
        Assert.Equal(3, receiver.Requests.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_worker_sends_no_more_requests_at_the_same_time_than_it_is_allowed_to(int parallelism)
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync(o =>
        {
            o.BatchSize = 6;
            o.MaxParallelism = parallelism;
        });
        for (var i = 0; i < 6; i++)
        {
            await h.SeedAsync($"{receiver.BaseUrl}/hold");
        }

        Assert.Equal(6, await h.Processor.ProcessBatchAsync(default));

        // Six requests were due and each takes a moment, so the most that overlapped is the limit itself.
        Assert.Equal(parallelism, receiver.MaxConcurrent);
    }

    [Fact]
    public async Task A_worker_takes_no_more_than_one_batch_at_a_time()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync(o => o.BatchSize = 2);
        for (var i = 0; i < 5; i++)
        {
            await h.SeedAsync($"{receiver.BaseUrl}/ok");
        }

        Assert.Equal(2, await h.Processor.ProcessBatchAsync(default));
        Assert.Equal(2, await h.Processor.ProcessBatchAsync(default));
        Assert.Equal(1, await h.Processor.ProcessBatchAsync(default));
        Assert.Equal(0, await h.Processor.ProcessBatchAsync(default));
        Assert.Equal(5, receiver.Requests.Count);
    }

    [Fact]
    public async Task A_receiver_that_is_too_slow_counts_as_a_failure()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync(o => o.RequestTimeout = TimeSpan.FromMilliseconds(500));
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/slow");

        await h.Processor.ProcessBatchAsync(default);

        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);

        var attempt = Assert.Single(await h.GetAttemptsAsync(seeded.DeliveryId));
        Assert.Null(attempt.ResponseStatus);
        Assert.Contains("Timed out", attempt.Error);
    }

    [Fact]
    public async Task A_receiver_that_cannot_be_reached_counts_as_a_failure()
    {
        await using var h = await CreateHarnessAsync();
        var seeded = await h.SeedAsync("http://127.0.0.1:1/hook"); // nothing listens on port 1

        await h.Processor.ProcessBatchAsync(default);

        var delivery = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);

        var attempt = Assert.Single(await h.GetAttemptsAsync(seeded.DeliveryId));
        Assert.Null(attempt.ResponseStatus);
        Assert.False(string.IsNullOrWhiteSpace(attempt.Error));
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync();
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/redirect");

        await h.Processor.ProcessBatchAsync(default);

        // Only the original address was contacted, never the redirect target.
        var request = Assert.Single(receiver.Requests);
        Assert.Equal("/redirect", request.Path);

        var attempt = Assert.Single(await h.GetAttemptsAsync(seeded.DeliveryId));
        Assert.Equal(302, attempt.ResponseStatus);
        Assert.Equal(DeliveryStatus.Pending, (await h.GetDeliveryAsync(seeded.DeliveryId)).Status);
    }

    [Fact]
    public async Task An_inactive_endpoint_receives_nothing_and_the_attempt_is_recorded()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync();
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok", active: false);

        await h.Processor.ProcessBatchAsync(default);

        Assert.Empty(receiver.Requests);
        var attempt = Assert.Single(await h.GetAttemptsAsync(seeded.DeliveryId));
        Assert.Equal("Endpoint is inactive.", attempt.Error);
    }

    [Fact]
    public async Task A_delivery_abandoned_by_a_crashed_worker_is_picked_up_after_its_lock_expires()
    {
        await using var receiver = await TestReceiver.StartAsync();
        await using var h = await CreateHarnessAsync(); // lock lasts 1 minute
        var seeded = await h.SeedAsync($"{receiver.BaseUrl}/ok");

        // Simulate a worker that claimed the delivery and then died without reporting back.
        await using (var db = h.CreateContext())
        {
            var delivery = db.Deliveries.Single(d => d.Id == seeded.DeliveryId);
            delivery.StartAttempt(h.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await h.Processor.ProcessBatchAsync(default)); // lock still valid
        Assert.Empty(receiver.Requests);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await h.Processor.ProcessBatchAsync(default));

        var result = await h.GetDeliveryAsync(seeded.DeliveryId);
        Assert.Equal(DeliveryStatus.Succeeded, result.Status);
        Assert.Equal(2, result.AttemptCount);
    }

    [Fact]
    public async Task Two_workers_never_send_the_same_delivery()
    {
        const int total = 30;
        await using var receiver = await TestReceiver.StartAsync();
        await using var first = await CreateHarnessAsync(o => o.BatchSize = 3);
        await using var second = first.CreateSecondWorker();

        var seeded = new List<Seeded>();
        for (var i = 0; i < total; i++)
        {
            seeded.Add(await first.SeedAsync($"{receiver.BaseUrl}/ok"));
        }

        async Task<int> Drain(DispatchHarness worker)
        {
            // A claim that never runs out of work would spin for ever and hang the whole test run (and CI with it),
            // so the loop has a limit and fails instead.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var handled = 0;
            int claimed;
            while ((claimed = await worker.Processor.ProcessBatchAsync(default)) > 0)
            {
                handled += claimed;
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"The worker was still being handed work after {handled} deliveries.");
                }
            }

            return handled;
        }

        var counts = await Task.WhenAll(Drain(first), Drain(second));

        // Every delivery was claimed exactly once in total...
        Assert.Equal(total, counts.Sum());

        // ...and the receiver saw each message exactly once.
        var ids = receiver.Requests.Select(r => r.Headers["Webhook-Id"]).ToList();
        Assert.Equal(total, ids.Count);
        Assert.Equal(total, ids.Distinct().Count());

        foreach (var s in seeded)
        {
            var delivery = await first.GetDeliveryAsync(s.DeliveryId);
            Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
            Assert.Equal(1, delivery.AttemptCount);
        }
    }
}
