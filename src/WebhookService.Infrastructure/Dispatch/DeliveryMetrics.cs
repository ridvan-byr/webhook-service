using System.Diagnostics.Metrics;
using WebhookService.Infrastructure.Dispatch.RabbitMq;

namespace WebhookService.Infrastructure.Dispatch;

/// <summary>
/// What the worker reports about itself, with the .NET metrics API (no extra package: anything that understands it,
/// such as OpenTelemetry or dotnet-counters, can collect it). Tags carry only a short fixed set of words, never an
/// id, an address or a tenant: a value that can take unboundedly many forms would break the system that stores the
/// numbers, and would leak what they have no business knowing.
/// </summary>
public sealed class DeliveryMetrics : IDisposable
{
    public const string MeterName = "WebhookService";

    private readonly Meter _meter;
    private readonly Counter<long> _attempts;
    private readonly Histogram<double> _attemptDuration;
    private readonly Histogram<double> _pickupDelay;
    private readonly Counter<long> _leased;
    private readonly Counter<long> _unconfirmed;
    private readonly Counter<long> _messageOutcomes;

    public DeliveryMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _attempts = _meter.CreateCounter<long>(
            "webhook.delivery.attempts",
            description: "Delivery attempts that were finished, by outcome: succeeded, failed (will be retried), dead (attempts used up) or discarded (another worker took over).");
        _attemptDuration = _meter.CreateHistogram<double>(
            "webhook.delivery.attempt.duration",
            unit: "ms",
            description: "How long the request to the receiver took.");
        _pickupDelay = _meter.CreateHistogram<double>(
            "webhook.delivery.pickup.delay",
            unit: "ms",
            description: "How long a delivery waited after it was due before an attempt started. The number to watch: it grows when workers cannot keep up.");
        _leased = _meter.CreateCounter<long>(
            "webhook.relay.leased",
            description: "Deliveries the relay found due and announced (or tried to).");
        _unconfirmed = _meter.CreateCounter<long>(
            "webhook.relay.unconfirmed",
            description: "Announcements the broker did not confirm, or that could not be sent. Their deliveries are due again at once.");
        _messageOutcomes = _meter.CreateCounter<long>(
            "webhook.message.outcomes",
            description: "What became of each message a worker received, by outcome: handled, ignored, poison or claim_failed.");
    }

    public void AttemptFinished(string outcome, double durationMs, double pickupDelayMs)
    {
        _attempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _attemptDuration.Record(durationMs);
        _pickupDelay.Record(Math.Max(0, pickupDelayMs));
    }

    public void AttemptDiscarded() => _attempts.Add(1, new KeyValuePair<string, object?>("outcome", Outcomes.Discarded));

    public void RelayPass(int leased, int unconfirmed)
    {
        _leased.Add(leased);
        if (unconfirmed > 0)
        {
            _unconfirmed.Add(unconfirmed);
        }
    }

    public void MessageHandled(MessageOutcome outcome) => _messageOutcomes.Add(
        1,
        new KeyValuePair<string, object?>("outcome", outcome switch
        {
            MessageOutcome.Handled => "handled",
            MessageOutcome.Ignored => "ignored",
            MessageOutcome.Poison => "poison",
            MessageOutcome.ClaimFailed => "claim_failed",
            _ => "unknown",
        }));

    public void Dispose() => _meter.Dispose();

    public static class Outcomes
    {
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Dead = "dead";
        public const string Discarded = "discarded";
    }
}
