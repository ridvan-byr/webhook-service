namespace WebhookService.Infrastructure.Dispatch;

/// <summary>Source of randomness for retry delays. An interface so tests can make it predictable.</summary>
public interface IJitter
{
    /// <summary>A number between 0 and 1.</summary>
    double Next();
}

public sealed class RandomJitter : IJitter
{
    public double Next() => Random.Shared.NextDouble();
}
