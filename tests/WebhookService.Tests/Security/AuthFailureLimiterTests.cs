using System.Net;
using Microsoft.Extensions.Options;
using WebhookService.Api.Security;
using WebhookService.Tests.Dispatch;

namespace WebhookService.Tests.Security;

public class AuthFailureLimiterTests
{
    private readonly TestClock _clock = new();

    private AuthFailureLimiter Create(int maxFailures = 3, TimeSpan? window = null, int maxClients = 10_000) =>
        new(
            Options.Create(new AuthFailureLimitOptions
            {
                MaxFailures = maxFailures,
                Window = window ?? TimeSpan.FromMinutes(1),
                MaxTrackedClients = maxClients,
            }),
            _clock);

    // ---------- counting ----------

    [Fact]
    public void A_client_that_never_failed_is_not_blocked()
    {
        var limiter = Create();

        Assert.False(limiter.IsBlocked("203.0.113.7", out _));
    }

    [Fact]
    public void A_client_is_blocked_after_the_allowed_number_of_failures_and_not_before()
    {
        var limiter = Create(maxFailures: 3);

        limiter.RecordFailure("203.0.113.7");
        limiter.RecordFailure("203.0.113.7");
        Assert.False(limiter.IsBlocked("203.0.113.7", out _));

        limiter.RecordFailure("203.0.113.7");
        Assert.True(limiter.IsBlocked("203.0.113.7", out _));
    }

    [Fact]
    public void Only_the_failure_that_reaches_the_limit_says_so_which_is_when_to_write_a_log_line()
    {
        var limiter = Create(maxFailures: 2);

        Assert.False(limiter.RecordFailure("c"));
        Assert.True(limiter.RecordFailure("c"));
        Assert.False(limiter.RecordFailure("c"));
    }

    [Fact]
    public void Clients_are_counted_separately()
    {
        var limiter = Create(maxFailures: 2);

        limiter.RecordFailure("a");
        limiter.RecordFailure("a");

        Assert.True(limiter.IsBlocked("a", out _));
        Assert.False(limiter.IsBlocked("b", out _));
    }

    // ---------- time ----------

    [Fact]
    public void The_wait_is_the_time_until_the_oldest_failure_leaves_the_window()
    {
        var limiter = Create(maxFailures: 3, window: TimeSpan.FromSeconds(60));
        limiter.RecordFailure("c");
        _clock.Advance(TimeSpan.FromSeconds(10));
        limiter.RecordFailure("c");
        _clock.Advance(TimeSpan.FromSeconds(10));
        limiter.RecordFailure("c");

        Assert.True(limiter.IsBlocked("c", out var retryAfter));

        // The first failure was 20 seconds ago, so it counts for another 40.
        Assert.Equal(TimeSpan.FromSeconds(40), retryAfter);
    }

    [Fact]
    public void The_wait_is_never_less_than_a_second_so_a_client_is_not_told_to_retry_at_once()
    {
        var limiter = Create(maxFailures: 1, window: TimeSpan.FromSeconds(60));
        limiter.RecordFailure("c");
        _clock.Advance(TimeSpan.FromMilliseconds(59_900));

        Assert.True(limiter.IsBlocked("c", out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(1), retryAfter);
    }

    [Fact]
    public void A_client_is_let_in_again_when_its_failures_are_older_than_the_window()
    {
        var limiter = Create(maxFailures: 2, window: TimeSpan.FromSeconds(60));
        limiter.RecordFailure("c");
        limiter.RecordFailure("c");
        Assert.True(limiter.IsBlocked("c", out _));

        _clock.Advance(TimeSpan.FromSeconds(60));

        Assert.False(limiter.IsBlocked("c", out _));
    }

    [Fact]
    public void The_window_slides_so_failures_at_the_edge_of_two_windows_do_not_both_count_as_a_fresh_start()
    {
        var limiter = Create(maxFailures: 3, window: TimeSpan.FromSeconds(60));
        limiter.RecordFailure("c");
        limiter.RecordFailure("c");
        _clock.Advance(TimeSpan.FromSeconds(59));
        // Two failures a moment ago, then one more: three within the last minute.
        limiter.RecordFailure("c");

        Assert.True(limiter.IsBlocked("c", out _));
    }

    [Fact]
    public void Failures_from_before_the_window_are_forgotten()
    {
        var limiter = Create(maxFailures: 3, window: TimeSpan.FromSeconds(60));
        limiter.RecordFailure("c");
        limiter.RecordFailure("c");
        _clock.Advance(TimeSpan.FromSeconds(61));

        limiter.RecordFailure("c");

        Assert.False(limiter.IsBlocked("c", out _));
    }

    // ---------- who the client is ----------

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    public void A_client_is_its_address_and_for_IPv6_its_whole_64_bit_network(string address, string expected)
    {
        Assert.Equal(expected, AuthFailureLimiter.ClientKey(IPAddress.Parse(address)));
    }

    [Fact]
    public void Addresses_in_different_IPv6_networks_are_different_clients()
    {
        Assert.NotEqual(
            AuthFailureLimiter.ClientKey(IPAddress.Parse("2001:db8:1:2::1")),
            AuthFailureLimiter.ClientKey(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void An_unknown_address_is_one_shared_client_instead_of_no_limit_at_all()
    {
        Assert.Equal("unknown", AuthFailureLimiter.ClientKey(null));
    }

    // ---------- memory ----------

    [Fact]
    public void The_number_of_remembered_clients_has_a_ceiling()
    {
        var limiter = Create(maxClients: 100);

        for (var i = 0; i < 1_000; i++)
        {
            limiter.RecordFailure($"client-{i}");
        }

        Assert.True(limiter.TrackedClients <= 100);
    }

    [Fact]
    public void Clients_whose_failures_have_expired_are_dropped_to_make_room()
    {
        var limiter = Create(maxClients: 100, window: TimeSpan.FromSeconds(60));
        for (var i = 0; i < 100; i++)
        {
            limiter.RecordFailure($"old-{i}");
        }

        _clock.Advance(TimeSpan.FromSeconds(61));
        limiter.RecordFailure("new");

        Assert.True(limiter.IsBlocked("new", out _) == false);
        Assert.True(limiter.TrackedClients <= 100);
        // The new client was remembered, so it can be blocked.
        limiter.RecordFailure("new");
        limiter.RecordFailure("new");
        Assert.True(limiter.IsBlocked("new", out _));
    }

    [Fact]
    public void A_client_that_is_already_remembered_stays_counted_when_the_ceiling_is_reached()
    {
        var limiter = Create(maxFailures: 2, maxClients: 5);
        limiter.RecordFailure("known");
        for (var i = 0; i < 100; i++)
        {
            limiter.RecordFailure($"other-{i}");
        }

        limiter.RecordFailure("known");

        Assert.True(limiter.IsBlocked("known", out _));
    }

    // ---------- threads ----------

    [Fact]
    public void Failures_remembered_for_one_client_never_exceed_the_allowed_number()
    {
        // Requests that were already past the check when the client became blocked can still add failures.
        var limiter = Create(maxFailures: 3);

        for (var i = 0; i < 1_000; i++)
        {
            limiter.RecordFailure("c");
        }

        Assert.Equal(3, limiter.TrackedFailures("c"));
    }

    [Fact]
    public void Failures_recorded_at_the_same_moment_are_all_counted()
    {
        var limiter = Create(maxFailures: 50);

        Parallel.For(0, 49, _ => limiter.RecordFailure("c"));
        Assert.False(limiter.IsBlocked("c", out _));

        limiter.RecordFailure("c");
        Assert.True(limiter.IsBlocked("c", out _));
    }

    // ---------- settings ----------

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(5, 0)]
    [InlineData(5, -10)]
    public void Settings_that_would_switch_the_limit_off_or_make_no_sense_stop_the_service(int maxFailures, int seconds)
    {
        var options = new AuthFailureLimitOptions { MaxFailures = maxFailures, Window = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_ceiling_below_one_client_stops_the_service_too(int maxTrackedClients)
    {
        var options = new AuthFailureLimitOptions { MaxTrackedClients = maxTrackedClients };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void The_defaults_are_valid_and_allow_twenty_failures_a_minute()
    {
        var options = new AuthFailureLimitOptions();

        options.Validate();
        Assert.Equal(20, options.MaxFailures);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Window);
    }
}
