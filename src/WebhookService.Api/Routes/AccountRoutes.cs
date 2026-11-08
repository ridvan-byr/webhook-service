using System.Security.Claims;
using WebhookService.Api.Contracts;
using WebhookService.Api.Security;
using WebhookService.Core.Domain;
using WebhookService.Core.Security;
using WebhookService.Infrastructure.Services;

namespace WebhookService.Api.Routes;

/// <summary>Tenant creation (operator only) and API key management (tenant).</summary>
internal static class AccountRoutes
{
    private const string AdminKeyHeader = "X-Admin-Key";

    public static void MapAccountRoutes(this IEndpointRouteBuilder app)
    {
        // Authenticated with the operator's admin key, not with a tenant API key.
        app.MapPost("/admin/tenants", CreateTenantAsync)
            .AllowAnonymous()
            .WithTags("Admin")
            .WithName("CreateTenant");

        var keys = app.MapGroup("/api-keys").WithTags("API keys");
        keys.MapPost("/", CreateKeyAsync).WithName("CreateApiKey");
        keys.MapGet("/", ListKeysAsync).WithName("ListApiKeys");
        keys.MapDelete("/{id:guid}", RevokeKeyAsync).WithName("RevokeApiKey");
    }

    private static async Task<IResult> CreateTenantAsync(
        HttpRequest request,
        CreateTenantRequest body,
        IConfiguration configuration,
        ApiKeyService keys,
        CancellationToken ct)
    {
        // No admin key configured means the feature is switched off. It looks like the route does not exist.
        var adminKey = configuration["Admin:ApiKey"];
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            return Results.NotFound();
        }

        var provided = request.Headers[AdminKeyHeader].ToString();
        if (!ApiKeyGenerator.SecretsEqual(adminKey, provided))
        {
            return Results.Problem(
                title: "Invalid admin key.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 200)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Is required and must be at most 200 characters."],
            });
        }

        var (tenant, issued) = await keys.CreateTenantAsync(body.Name, ct);

        return Results.Json(
            new TenantCreatedResponse(tenant.Id, tenant.Name, issued.Key.Id, issued.PlainText),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> CreateKeyAsync(
        ClaimsPrincipal user,
        CreateApiKeyRequest body,
        ApiKeyService keys,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Is required and must be at most 100 characters."],
            });
        }

        var issued = await keys.IssueKeyAsync(user.TenantId(), body.Name, ct);

        return Results.Json(
            new ApiKeyCreatedResponse(
                issued.Key.Id, issued.Key.Name, issued.Key.Prefix, issued.Key.CreatedAt, issued.PlainText),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListKeysAsync(ClaimsPrincipal user, ApiKeyService keys, CancellationToken ct)
    {
        var list = await keys.ListAsync(user.TenantId(), ct);

        return Results.Ok(list.Select(ToSummary));
    }

    private static async Task<IResult> RevokeKeyAsync(
        Guid id,
        ClaimsPrincipal user,
        ApiKeyService keys,
        CancellationToken ct)
    {
        var outcome = await keys.RevokeAsync(user.TenantId(), id, user.ApiKeyId(), ct);

        return outcome switch
        {
            RevokeOutcome.Revoked => Results.NoContent(),
            RevokeOutcome.KeyInUse => Results.Problem(
                title: "This key is in use.",
                detail: "You cannot revoke the key that authenticates this request. Use another key.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.NotFound(),
        };
    }

    private static ApiKeySummaryResponse ToSummary(ApiKey k) =>
        new(k.Id, k.Name, k.Prefix, k.CreatedAt, k.RevokedAt);
}
