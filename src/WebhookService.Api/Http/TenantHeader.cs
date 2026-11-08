namespace WebhookService.Api.Http;

/// <summary>
/// TEMPORARY tenant identification. The caller simply states its tenant id in a header, which
/// anyone could fake. It exists so the endpoints can be built and tested before authentication
/// is added. It is replaced by API keys in v0.2 (see docs/decisions/0003).
/// </summary>
internal static class TenantHeader
{
    public const string Name = "X-Tenant-Id";

    public static bool TryRead(HttpRequest request, out Guid tenantId) =>
        Guid.TryParse(request.Headers[Name].ToString(), out tenantId) && tenantId != Guid.Empty;
}
