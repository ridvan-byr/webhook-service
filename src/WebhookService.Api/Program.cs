using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Routes;
using WebhookService.Api.Security;
using WebhookService.Infrastructure.Persistence;
using WebhookService.Infrastructure.Security;
using WebhookService.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton(TimeProvider.System);

// The connection string is read when the context is first needed (not at startup), so a
// missing database setting does not stop endpoints that do not use the database, like /health.
builder.Services.AddDbContext<WebhookDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>()
        .GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

    options.UseNpgsql(connectionString);
});

builder.Services.Configure<NetworkSecurityOptions>(builder.Configuration.GetSection(NetworkSecurityOptions.SectionName));

// Too many failed attempts to authenticate from one client are refused for a while.
builder.Services.Configure<AuthFailureLimitOptions>(builder.Configuration.GetSection(AuthFailureLimitOptions.SectionName));
builder.Services.AddSingleton<AuthFailureLimiter>();

// The limit counts clients by address. Behind a reverse proxy every request comes from the proxy, so the
// address it reports in X-Forwarded-For is believed, but only from the networks listed here. By default none
// is, and the header is ignored: otherwise anyone could pick a new address with every request.
var trustedProxyNetworks = builder.Configuration.GetSection("Security:TrustedProxyNetworks").Get<string[]>() ?? [];
if (trustedProxyNetworks.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in trustedProxyNetworks)
        {
            options.KnownNetworks.Add(ParseNetwork(network));
        }
    });
}

builder.Services.AddScoped<EventIngestionService>();
builder.Services.AddScoped<ApiKeyService>();

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });

// Secure by default: every route needs a valid API key unless it explicitly opts out with AllowAnonymous().
// A route added later and forgotten is therefore protected, not open.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

var app = builder.Build();

// Created now and not at the first request, so a limit that was set to something that makes no sense stops
// the service at start instead of leaving it unprotected.
app.Services.GetRequiredService<AuthFailureLimiter>();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<WebhookDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (trustedProxyNetworks.Length > 0)
{
    // Before authentication, which counts failures by the address of the client.
    app.UseForwardedHeaders();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health");

app.MapAccountRoutes();
app.MapEndpointRoutes();
app.MapEventRoutes();
app.MapDeliveryRoutes();

app.Run();

static Microsoft.AspNetCore.HttpOverrides.IPNetwork ParseNetwork(string text)
{
    var parts = text.Split('/');
    if (parts.Length != 2
        || !IPAddress.TryParse(parts[0], out var address)
        || !int.TryParse(parts[1], out var prefixLength)
        || prefixLength < 0
        || prefixLength > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
    {
        throw new InvalidOperationException(
            $"Security:TrustedProxyNetworks: '{text}' is not a network such as 10.0.0.0/8.");
    }

    return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefixLength);
}

// Integration tests (WebApplicationFactory) need access to the entry point.
public partial class Program;
