using Microsoft.EntityFrameworkCore;
using WebhookService.Core.Domain;
using WebhookService.Core.Security;
using WebhookService.Infrastructure.Persistence;

namespace WebhookService.Infrastructure.Services;

/// <param name="PlainText">The key itself. It exists only in memory and in the response that creates it.</param>
public sealed record IssuedKey(ApiKey Key, string PlainText);

public sealed record AuthenticatedKey(Guid TenantId, Guid KeyId);

/// <summary>Who a key belongs to, in words a person can read. Never contains the key itself.</summary>
public sealed record KeyIdentity(Guid TenantId, string TenantName, Guid ApiKeyId, string ApiKeyName);

public enum RevokeOutcome
{
    Revoked,
    NotFound,

    /// <summary>The caller tried to revoke the key it is using for this very request.</summary>
    KeyInUse,
}

public sealed class ApiKeyService
{
    private readonly WebhookDbContext _db;
    private readonly TimeProvider _time;

    public ApiKeyService(WebhookDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>Creates a tenant together with its first key, in one transaction.</summary>
    public async Task<(Tenant Tenant, IssuedKey Key)> CreateTenantAsync(string name, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var tenant = Tenant.Create(name, now);
        var issued = NewKey(tenant.Id, "default", now);

        _db.Tenants.Add(tenant);
        _db.ApiKeys.Add(issued.Key);
        await _db.SaveChangesAsync(ct);

        return (tenant, issued);
    }

    public async Task<IssuedKey> IssueKeyAsync(Guid tenantId, string name, CancellationToken ct)
    {
        var issued = NewKey(tenantId, name, _time.GetUtcNow());

        _db.ApiKeys.Add(issued.Key);
        await _db.SaveChangesAsync(ct);

        return issued;
    }

    /// <summary>Returns the owner of a key, or null when the key is unknown, wrong or revoked.</summary>
    public async Task<AuthenticatedKey?> AuthenticateAsync(string? presentedKey, CancellationToken ct)
    {
        if (!ApiKeyGenerator.TryGetPrefix(presentedKey, out var prefix))
        {
            return null;
        }

        var stored = await _db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Prefix == prefix, ct);

        // Same answer for "no such key", "wrong secret" and "revoked": callers learn nothing extra.
        if (stored is null || stored.IsRevoked || !ApiKeyGenerator.Matches(presentedKey!, stored.KeyHash))
        {
            return null;
        }

        return new AuthenticatedKey(stored.TenantId, stored.Id);
    }

    public Task<KeyIdentity?> DescribeAsync(Guid tenantId, Guid keyId, CancellationToken ct) =>
        (from k in _db.ApiKeys.AsNoTracking()
         join t in _db.Tenants.AsNoTracking() on k.TenantId equals t.Id
         where k.Id == keyId && k.TenantId == tenantId
         select new KeyIdentity(t.Id, t.Name, k.Id, k.Name))
        .SingleOrDefaultAsync(ct);

    public Task<List<ApiKey>> ListAsync(Guid tenantId, CancellationToken ct) =>
        _db.ApiKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .ToListAsync(ct);

    public async Task<RevokeOutcome> RevokeAsync(Guid tenantId, Guid keyId, Guid currentKeyId, CancellationToken ct)
    {
        // Filtering by tenant means another tenant's key looks like it does not exist.
        var key = await _db.ApiKeys.SingleOrDefaultAsync(k => k.Id == keyId && k.TenantId == tenantId, ct);
        if (key is null)
        {
            return RevokeOutcome.NotFound;
        }

        // Revoking the key you are using is almost always a mistake and would lock a tenant out
        // if it was their only key.
        if (key.Id == currentKeyId)
        {
            return RevokeOutcome.KeyInUse;
        }

        key.Revoke(_time.GetUtcNow());
        await _db.SaveChangesAsync(ct);

        return RevokeOutcome.Revoked;
    }

    private static IssuedKey NewKey(Guid tenantId, string name, DateTimeOffset now)
    {
        var generated = ApiKeyGenerator.Generate();
        var key = ApiKey.Create(tenantId, name, generated.Prefix, generated.Hash, now);
        return new IssuedKey(key, generated.PlainText);
    }
}
