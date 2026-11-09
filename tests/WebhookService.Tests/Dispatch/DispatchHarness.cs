using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebhookService.Core.Domain;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Persistence;
using WebhookService.Infrastructure.Security;
using WebhookService.Tests.Persistence;
using WebhookService.Worker;

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
        // Everything is kept in memory so a test can say what was logged, and nothing is written out.
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new CapturingLoggerProvider(Logs));
        });
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
        Processor = new BatchDriver(
            _services.GetRequiredService<PostgresDeliverySource>(),
            _services.GetRequiredService<DeliveryProcessor>());
        Enqueue = _services.GetRequiredService<DeliveryEnqueueStore>();
        Configure = configure;
    }

    /// <summary>Finds the deliveries to announce and puts a lease on them (the database half of a message transport).</summary>
    public DeliveryEnqueueStore Enqueue { get; }

    public TestClock Clock { get; }

    /// <summary>What the worker and its parts logged, at Information and above.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<LogLine> Logs { get; } = new();

    /// <summary>Runs deliveries one batch at a time, so a test decides exactly when work happens.</summary>
    public BatchDriver Processor { get; }

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

    /// <summary>The worker process's background service, wired up the way the host wires it.</summary>
    public DeliveryWorker CreateWorker() => ActivatorUtilities.CreateInstance<DeliveryWorker>(_services);

    /// <summary>
    /// Behaves like another worker that is in the middle of claiming: locks every delivery row and holds the lock
    /// until the returned object is rolled back or disposed.
    /// </summary>
    public async Task<RowLock> LockDeliveriesAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM deliveries FOR UPDATE", connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        return new RowLock(connection, transaction);
    }

    /// <summary>Runs one SQL statement on this harness's own database, for tests that break it on purpose.</summary>
    public async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

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

    public async Task<List<Delivery>> GetAllDeliveriesAsync()
    {
        await using var db = CreateContext();
        return await db.Deliveries.AsNoTracking().ToListAsync();
    }

    /// <summary>Changes a delivery through its own methods, to put it in a state a test needs.</summary>
    public async Task ModifyDeliveryAsync(Guid id, Action<Delivery> change)
    {
        await using var db = CreateContext();
        var delivery = await db.Deliveries.SingleAsync(d => d.Id == id);
        change(delivery);
        await db.SaveChangesAsync();
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

public sealed record LogLine(LogLevel Level, string Category, string Message);

/// <summary>Row locks held by a transaction of the test. Releasing them is a rollback.</summary>
public sealed class RowLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public RowLock(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public Task ReleaseAsync() => _transaction.RollbackAsync();

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogLine> _lines;

    public CapturingLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<LogLine> lines) => _lines = lines;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogLine> _lines;

        public CapturingLogger(string category, System.Collections.Concurrent.ConcurrentQueue<LogLine> lines)
        {
            _category = category;
            _lines = lines;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue(new LogLine(logLevel, _category, formatter(state, exception)));
    }
}

/// <summary>
/// Makes a transport hand over what is due right now and runs it through the processor, once. The contract tests
/// call only this, so another transport can be tested by giving it a driver of its own.
/// </summary>
public sealed class BatchDriver
{
    private readonly PostgresDeliverySource _source;
    private readonly DeliveryProcessor _processor;

    public BatchDriver(PostgresDeliverySource source, DeliveryProcessor processor)
    {
        _source = source;
        _processor = processor;
    }

    /// <returns>How many deliveries were handed over. Zero means nothing was due.</returns>
    public Task<int> ProcessBatchAsync(CancellationToken ct) => _source.ProcessBatchAsync(_processor.DeliverAsync, ct);
}
