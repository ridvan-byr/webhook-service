using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using WebhookService.Infrastructure.Services;

namespace WebhookService.Api.Security;

/// <summary>
/// Reads "Authorization: Bearer whk_..." and turns a valid key into a signed-in tenant.
/// Nothing about the tenant is taken from the request itself: the tenant comes from the stored key.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    private const string BearerPrefix = "Bearer ";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Unsupported authorization scheme.");
        }

        // Looked up only now, so a request without credentials (like /health) never touches the database.
        var keys = Context.RequestServices.GetRequiredService<ApiKeyService>();
        var authenticated = await keys.AuthenticateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (authenticated is null)
        {
            // The reason is deliberately not logged or returned: it could help someone guess keys.
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(CurrentUser.TenantIdClaim, authenticated.TenantId.ToString()),
                new Claim(CurrentUser.ApiKeyIdClaim, authenticated.KeyId.ToString()),
            ],
            SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
