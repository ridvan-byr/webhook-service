using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using WebhookService.Infrastructure.Services;

namespace WebhookService.Api.Security;

/// <summary>
/// Reads "Authorization: Bearer whk_..." and turns a valid key into a signed-in tenant.
/// Nothing about the tenant is taken from the request itself: the tenant comes from the stored key.
///
/// A client that keeps presenting keys that are wrong is refused for a while (<see cref="AuthFailureLimiter"/>),
/// without checking the key at all.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    private const string BearerPrefix = "Bearer ";

    // Set when a request is refused because of the limit, so the challenge answers 429 and not 401.
    private const string RetryAfterItem = "WebhookService.AuthFailureRetryAfter";

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
            // No credentials were presented, so nothing was guessed and nothing is counted.
            return AuthenticateResult.NoResult();
        }

        var limiter = Context.RequestServices.GetRequiredService<AuthFailureLimiter>();
        var client = AuthFailureLimiter.ClientKey(Context.Connection.RemoteIpAddress);

        // Before anything is looked up: a client over the limit costs the service next to nothing.
        if (limiter.IsBlocked(client, out var retryAfter))
        {
            Context.Items[RetryAfterItem] = retryAfter;
            return AuthenticateResult.Fail("Too many failed attempts.");
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            RecordFailure(limiter, client);
            return AuthenticateResult.Fail("Unsupported authorization scheme.");
        }

        // Looked up only now, so a request without credentials (like /health) never touches the database.
        var keys = Context.RequestServices.GetRequiredService<ApiKeyService>();
        var authenticated = await keys.AuthenticateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (authenticated is null)
        {
            RecordFailure(limiter, client);

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
        if (Context.Items.TryGetValue(RetryAfterItem, out var retryAfter) && retryAfter is TimeSpan wait)
        {
            return RateLimitedResponse.WriteAsync(Response, wait);
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private void RecordFailure(AuthFailureLimiter limiter, string client)
    {
        if (limiter.RecordFailure(client))
        {
            // Said once, when the limit is reached. It names the client, never what was presented.
            Logger.LogWarning(
                "Too many failed attempts to authenticate from {Client}. Further attempts are refused for a while.",
                client);
        }
    }
}
