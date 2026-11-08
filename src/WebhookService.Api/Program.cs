using Microsoft.AspNetCore.Authentication;
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

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .WithName("Health")
    .WithOpenApi();

app.MapAccountRoutes();
app.MapEndpointRoutes();
app.MapEventRoutes();
app.MapDeliveryRoutes();

app.Run();

// Integration tests (WebApplicationFactory) need access to the entry point.
public partial class Program;
