namespace WebhookService.Api.Contracts;

public sealed record CreateTenantRequest(string? Name);

/// <param name="ApiKey">The key itself. Shown only in this response and impossible to look up later.</param>
public sealed record TenantCreatedResponse(Guid TenantId, string Name, Guid ApiKeyId, string ApiKey);

public sealed record CreateApiKeyRequest(string? Name);

/// <param name="ApiKey">The key itself. Shown only in this response.</param>
public sealed record ApiKeyCreatedResponse(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, string ApiKey);

/// <summary>What the owner can see later. The key itself is never included, only its public prefix.</summary>
public sealed record ApiKeySummaryResponse(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
