using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace WebhookService.Tests.Api;

/// <summary>Runs the real API in memory, connected to the test PostgreSQL container.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// A request carrying this header is treated as coming from that address. The in-memory server has no
    /// network connection, so without it every request would come from "nowhere".
    /// </summary>
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    private readonly string _connectionString;
    private readonly string? _adminKey;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly TimeProvider? _clock;

    /// <param name="adminKey">Null means no admin key is configured, so tenant creation is switched off.</param>
    /// <param name="settings">Extra configuration, for example the failure limit.</param>
    /// <param name="clock">A clock the test controls. Null means the real one.</param>
    public ApiFactory(
        string connectionString,
        string? adminKey = ApiTestBase.AdminKey,
        IReadOnlyDictionary<string, string?>? settings = null,
        TimeProvider? clock = null)
    {
        _connectionString = connectionString;
        _adminKey = adminKey;
        _settings = settings ?? new Dictionary<string, string?>();
        _clock = clock;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" keeps appsettings.Development.json (local database settings) out of the tests.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);

        if (_adminKey is not null)
        {
            builder.UseSetting("Admin:ApiKey", _adminKey);
        }

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, RemoteAddressStartupFilter>();

            if (_clock is not null)
            {
                services.AddSingleton(_clock);
            }
        });
    }

    /// <summary>Runs before everything else in the pipeline and sets the address the request came from.</summary>
    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var value)
                        && IPAddress.TryParse(value.ToString(), out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    await nextMiddleware();
                });

                next(app);
            };
    }
}
