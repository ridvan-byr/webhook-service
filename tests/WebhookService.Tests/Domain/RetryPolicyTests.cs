using WebhookService.Core.Domain;

namespace WebhookService.Tests.Domain;

public class RetryPolicyTests
{
    private static readonly RetryPolicy Policy = new(
        MaxAttempts: 10,
        BaseDelay: TimeSpan.FromSeconds(10),
        MaxDelay: TimeSpan.FromMinutes(5));

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    public void With_full_jitter_the_delay_doubles_each_time(int attemptsMade, int expectedSeconds)
    {
        var delay = Policy.DelayAfter(attemptsMade, jitter: 1);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void With_no_jitter_the_delay_is_half_of_the_full_value()
    {
        var delay = Policy.DelayAfter(attemptsMade: 3, jitter: 0);

        Assert.Equal(TimeSpan.FromSeconds(20), delay);
    }

    [Fact]
    public void The_delay_never_exceeds_the_maximum()
    {
        var delay = Policy.DelayAfter(attemptsMade: 20, jitter: 1);

        Assert.Equal(Policy.MaxDelay, delay);
    }

    [Fact]
    public void A_very_large_attempt_number_does_not_overflow()
    {
        var delay = Policy.DelayAfter(attemptsMade: int.MaxValue, jitter: 1);

        Assert.Equal(Policy.MaxDelay, delay);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void The_delay_stays_between_half_and_the_full_value(double jitter)
    {
        var full = TimeSpan.FromSeconds(40);

        var delay = Policy.DelayAfter(attemptsMade: 3, jitter);

        Assert.InRange(delay, full / 2, full);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Attempt_numbers_below_one_are_rejected(int attemptsMade)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.DelayAfter(attemptsMade, jitter: 0.5));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Jitter_outside_zero_to_one_is_rejected(double jitter)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.DelayAfter(attemptsMade: 1, jitter));
    }
}
