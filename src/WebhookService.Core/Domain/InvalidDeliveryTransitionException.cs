namespace WebhookService.Core.Domain;

public sealed class InvalidDeliveryTransitionException : InvalidOperationException
{
    public InvalidDeliveryTransitionException(DeliveryStatus from, string action)
        : base($"Cannot {action} a delivery that is {from}.")
    {
        From = from;
        Action = action;
    }

    public DeliveryStatus From { get; }

    public string Action { get; }
}
