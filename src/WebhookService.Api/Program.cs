using Microsoft.EntityFrameworkCore;
using WebhookService.Api.Routes;
using WebhookService.Infrastructure.Persistence;
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

builder.Services.AddScoped<EventIngestionService>();

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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("Health")
    .WithOpenApi();

app.MapEndpointRoutes();
app.MapEventRoutes();

app.Run();

// Integration tests (WebApplicationFactory) need access to the entry point.
public partial class Program;
