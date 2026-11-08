using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Persistence;
using WebhookService.Infrastructure.Security;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Dispatch;

/// <summary>
/// A delivery processor wired up the way the worker host wires it, on its own empty database,
/// with a clock the test controls.
/// </summary>
public sealed class DispatchHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _connectionString;
    private readonly Action<NetworkSecurityOptions>? _security;
    private readonly IHostResolver? _resolver;

    private DispatchHarness(
        string connectionString,
        TestClock clock,
        Action<DeliveryOptions>? configure,
        Action<NetworkSecurityOptions>? security,
        IHostResolver? resolver)
    {
        _connectionString = connectionString;
        Clock = clock;
        _security = security;
        _resolver = resolver;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IJitter, FixedJitter>();
        if (resolver is not null)
        {
            services.AddSingleton(resolver);
        }

        services.AddWebhookDelivery(configuration);
        services.Configure<NetworkSecurityOptions>(o =>
        {
            // The test receiver lives on 127.0.0.1, so by default private networks are allowed here.
            // Security tests switch this off.
            o.AllowPrivateNetworks = true;
            o.RequireHttps = false;
            security?.Invoke(o);
        });
        services.Configure<DeliveryOptions>(o =>
        {
            o.MaxAttempts = 3;
            o.BaseDelay = TimeSpan.FromSeconds(10);
            o.MaxDelay = TimeSpan.FromMinutes(5);
            o.RequestTimeout = TimeSpan.FromSeconds(2);
            o.LockDuration = TimeSpan.FromMinutes(1);
            o.BatchSize = 10;
            configure?.Invoke(o);
        });

        _services = services.BuildServiceProvider();
        Processor = _services.GetRequiredService<DeliveryProcessor>();
        Configure = configure;
    }

    public TestClock Clock { get; }

    public DeliveryProcessor Processor { get; }

    private Action<DeliveryOptions>? Configure { get; }

    public static async Task<DispatchHarness> CreateAsync(
        PostgresFixture postgres,
        Action<DeliveryOptions>? configure = null,
        Action<NetworkSecurityOptions>? security = null,
        IHostResolver? resolver = null)
    {
        var connectionString = await postgres.CreateIsolatedDatabaseAsync();
        return new DispatchHarness(connectionString, new TestClock(), configure, security, resolver);
    }

    /// <summary>Another worker on the same database and the same clock, to test several workers at once.</summary>
    public DispatchHarness CreateSecondWorker() => new(_connectionString, Clock, Configure, _security, _resolver);

    public WebhookDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<WebhookDbContext>().UseNpgsql(_connectionString).Options);

    public async Task<Seeded> SeedAsync(string url, string payload = "{}", bool active = true)
    {
        var now = Clock.GetUtcNow();
        var tenant = Guid.NewGuid();
        var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var ev = Event.Create(tenant, "order.created", payload, null, now);
        var endpoint = Endpoint.Create(tenant, new Uri(url), secret, now);
        if (!active)
        {
            endpoint.Deactivate();
        }

        var delivery = Delivery.Create(ev.Id, endpoint.Id, now);

        await using var db = CreateContext();
        db.AddRange(ev, endpoint, delivery);
        await db.SaveChangesAsync();

        return new Seeded(delivery.Id, ev.Id, secret);
    }

    public async Task<Delivery> GetDeliveryAsync(Guid id)
    {
        await using var db = CreateContext();
        return await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    public async Task<List<DeliveryAttempt>> GetAttemptsAsync(Guid deliveryId)
    {
        await using var db = CreateContext();
        return await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.DeliveryId == deliveryId)
            .OrderBy(a => a.StartedAt)
            .ToListAsync();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}

public sealed record Seeded(Guid DeliveryId, Guid EventId, string Secret);
