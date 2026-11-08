using System.Security.Claims;

namespace WebhookService.Api.Security;

/// <summary>Reads the signed-in tenant. Only call this on routes that require authentication.</summary>
internal static class CurrentUser
{
    public const string TenantIdClaim = "tenant_id";
    public const string ApiKeyIdClaim = "api_key_id";

    public static Guid TenantId(this ClaimsPrincipal user) => ReadGuid(user, TenantIdClaim);

    public static Guid ApiKeyId(this ClaimsPrincipal user) => ReadGuid(user, ApiKeyIdClaim);

    private static Guid ReadGuid(ClaimsPrincipal user, string claim) =>
        Guid.TryParse(user.FindFirstValue(claim), out var id)
            ? id
            : throw new InvalidOperationException($"The '{claim}' claim is missing. The route must require authentication.");
}
