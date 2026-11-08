namespace WebhookService.Infrastructure.Security;

/// <summary>
/// Where webhooks may be sent. The defaults are the safe ones for a public deployment.
/// Both the API (when an endpoint is registered) and the worker (when connecting) read these.
/// </summary>
public sealed class NetworkSecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Allow localhost, private networks and other non-public addresses as destinations.
    /// Only for local development, where a receiver runs on the developer's machine.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Refuse plain http URLs. Turn off only for local development.</summary>
    public bool RequireHttps { get; set; } = true;
}
