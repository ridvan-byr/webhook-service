namespace WebhookService.Infrastructure.Dispatch;

/// <summary>How workers learn that a delivery is due. The database is the source of truth with either one.</summary>
public enum DeliveryTransport
{
    /// <summary>Workers poll the database. Needs nothing else.</summary>
    Postgres = 0,

    /// <summary>A relay announces due deliveries to RabbitMQ and workers consume the announcements.</summary>
    RabbitMq = 1,
}
