namespace WebhookService.Infrastructure.Dispatch.RabbitMq;

/// <summary>
/// Settings of the RabbitMQ transport, read from "Delivery:RabbitMq". There is deliberately no default user or
/// password: a default would be known to everyone who has read this file, and the broker's own "guest" account only
/// works from the same machine anyway. Both come from the environment.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "Delivery:RabbitMq";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Encrypts the connection to the broker. Turn it on whenever the broker is not on the same machine.</summary>
    public bool UseTls { get; set; }

    /// <summary>The exchange the relay publishes to.</summary>
    public string Exchange { get; set; } = "webhooks.deliveries";

    /// <summary>The queue the workers consume from.</summary>
    public string Queue { get; set; } = "webhooks.deliveries.due";

    /// <summary>Where messages that cannot be understood are put aside, instead of being tried again and again.</summary>
    public string PoisonQueue { get; set; } = "webhooks.deliveries.poison";

    /// <summary>How long the relay waits for the broker to confirm that it has taken a message.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        Require(Host, nameof(Host));
        Require(VirtualHost, nameof(VirtualHost));
        Require(Username, nameof(Username));
        Require(Password, nameof(Password));
        Require(Exchange, nameof(Exchange));
        Require(Queue, nameof(Queue));
        Require(PoisonQueue, nameof(PoisonQueue));

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(Port)} must be between 1 and 65535.");
        }

        if (PublishTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(PublishTimeout)} must be positive.");
        }

        if (new[] { Exchange, Queue, PoisonQueue }.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            throw new InvalidOperationException(
                $"{SectionName}: the exchange, the queue and the poison queue must have different names.");
        }
    }

    /// <summary>Never includes the password, so the options can be logged safely.</summary>
    public override string ToString() => $"{Username}@{Host}:{Port}{(VirtualHost == "/" ? "/" : "/" + VirtualHost)}";

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{SectionName}:{name} must be set.");
        }
    }
}
