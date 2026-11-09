using System.Net;
using System.Text;
using WebhookService.Tests.Dispatch;
using WebhookService.Tests.Persistence;

namespace WebhookService.Tests.Api;

/// <summary>Too many failed attempts to authenticate from one client are refused with 429.</summary>
[Collection(PostgresCollection.Name)]
public class AuthRateLimitApiTests : ApiTestBase
{
    private const string BadKey = "whk_000000000000_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly List<ApiFactory> _factories = [];
    private readonly TestClock _clock = new();

    public AuthRateLimitApiTests(PostgresFixture postgres)
        : base(postgres)
    {
    }

    public override void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        base.Dispose();
    }

    private HttpClient Start(
        int maxFailures = 3,
        string? adminKey = AdminKey,
        params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Security:AuthFailureLimit:MaxFailures"] = maxFailures.ToString(),
            ["Security:AuthFailureLimit:Window"] = "00:01:00",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        var factory = new ApiFactory(Postgres.ConnectionString, adminKey, settings, _clock);
        _factories.Add(factory);
        return factory.CreateClient();
    }

    private static HttpRequestMessage Get(string url, string? authorization, string? from = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (from is not null)
        {
            request.Headers.Add(ApiFactory.RemoteAddressHeader, from);
        }

        return request;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpRequestMessage request) =>
        (await client.SendAsync(request)).StatusCode;

    // ---------- the limit ----------

    [Fact]
    public async Task Wrong_keys_are_refused_with_401_until_the_limit_and_with_429_after_it()
    {
        var client = Start(maxFailures: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}")));
        }

        var response = await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task The_429_says_how_long_to_wait_and_nothing_else_about_why()
    {
        var client = Start(maxFailures: 1);
        await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));
        _clock.Advance(TimeSpan.FromSeconds(20));

        var response = await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(40), response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("whk_", body);
        Assert.DoesNotContain(BadKey, body);
    }

    [Fact]
    public async Task A_client_over_the_limit_is_refused_even_with_a_right_key_until_the_window_has_passed()
    {
        // Checking a key is what the limit protects, so a blocked client gets no answer about keys at all.
        var client = Start(maxFailures: 2);
        var goodKey = KeyFor(Tenant);
        await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));
        await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Get("/endpoints", $"Bearer {goodKey}")));

        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, Get("/endpoints", $"Bearer {goodKey}")));
    }

    [Fact]
    public async Task Another_client_is_not_affected()
    {
        var client = Start(maxFailures: 2);
        var goodKey = KeyFor(Tenant);
        for (var i = 0; i < 3; i++)
        {
            await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}", from: "203.0.113.7"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}", from: "203.0.113.7")));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, Get("/endpoints", $"Bearer {goodKey}", from: "203.0.113.8")));
    }

    [Fact]
    public async Task A_wrong_scheme_counts_as_a_failure_too()
    {
        var client = Start(maxFailures: 2);

        await client.SendAsync(Get("/endpoints", "Basic dXNlcjpwYXNz"));
        await client.SendAsync(Get("/endpoints", "Basic dXNlcjpwYXNz"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Get("/endpoints", "Basic dXNlcjpwYXNz")));
    }

    // ---------- what does not count ----------

    [Fact]
    public async Task Requests_without_any_credentials_are_never_counted()
    {
        var client = Start(maxFailures: 2);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, Get("/endpoints", authorization: null)));
        }

        // The counter is still at zero: one failure with a wrong key is still just a 401.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}")));
    }

    [Fact]
    public async Task Successful_requests_are_never_counted()
    {
        var client = Start(maxFailures: 2);
        var goodKey = KeyFor(Tenant);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, Get("/endpoints", $"Bearer {goodKey}")));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}")));
    }

    [Fact]
    public async Task A_right_key_in_between_does_not_reset_the_count()
    {
        var client = Start(maxFailures: 3);
        var goodKey = KeyFor(Tenant);

        for (var i = 0; i < 3; i++)
        {
            await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));
            await client.SendAsync(Get("/endpoints", $"Bearer {goodKey}"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}")));
    }

    [Fact]
    public async Task The_health_check_stays_open_for_a_blocked_client()
    {
        var client = Start(maxFailures: 1);
        await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}")));

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, Get("/health", authorization: null)));
    }

    // ---------- the admin key ----------

    private static HttpRequestMessage Admin(string key) =>
        new(HttpMethod.Post, "/admin/tenants")
        {
            Content = new StringContent("{\"name\":\"Acme\"}", Encoding.UTF8, "application/json"),
            Headers = { { "X-Admin-Key", key } },
        };

    [Fact]
    public async Task Wrong_admin_keys_are_limited_in_the_same_way()
    {
        var client = Start(maxFailures: 2);

        await client.SendAsync(Admin("wrong"));
        await client.SendAsync(Admin("wrong"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Admin("wrong")));
        // The right admin key gets no answer either while the client is over the limit.
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Admin(AdminKey)));
    }

    [Fact]
    public async Task A_request_without_the_admin_header_guessed_nothing_and_is_not_counted()
    {
        var client = Start(maxFailures: 2);

        for (var i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/admin/tenants")
            {
                Content = new StringContent("{\"name\":\"Acme\"}", Encoding.UTF8, "application/json"),
            };
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, request));
        }

        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, Admin(AdminKey)));
    }

    [Fact]
    public async Task A_right_admin_key_still_works_below_the_limit()
    {
        var client = Start(maxFailures: 3);
        await client.SendAsync(Admin("wrong"));

        Assert.Equal(HttpStatusCode.Created, await StatusAsync(client, Admin(AdminKey)));
    }

    [Fact]
    public async Task Failed_attempts_with_a_tenant_key_and_an_admin_key_share_one_count()
    {
        var client = Start(maxFailures: 2);

        await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));
        await client.SendAsync(Admin("wrong"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}")));
    }

    [Fact]
    public async Task When_there_is_no_admin_key_the_route_stays_hidden_and_nothing_is_counted()
    {
        var client = Start(maxFailures: 1, adminKey: null);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, Admin("anything")));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, Get("/endpoints", $"Bearer {BadKey}")));
    }

    // ---------- behind a proxy ----------

    [Fact]
    public async Task Behind_a_trusted_proxy_the_client_is_the_address_the_proxy_reports()
    {
        var client = Start(maxFailures: 2, extra: ("Security:TrustedProxyNetworks:0", "10.0.0.0/8"));
        var goodKey = KeyFor(Tenant);

        for (var i = 0; i < 2; i++)
        {
            var failure = Get("/endpoints", $"Bearer {BadKey}", from: "10.0.0.5");
            failure.Headers.Add("X-Forwarded-For", "203.0.113.7");
            await client.SendAsync(failure);
        }

        var blocked = Get("/endpoints", $"Bearer {BadKey}", from: "10.0.0.5");
        blocked.Headers.Add("X-Forwarded-For", "203.0.113.7");
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, blocked));

        // Someone else behind the same proxy is not punished for it.
        var other = Get("/endpoints", $"Bearer {goodKey}", from: "10.0.0.5");
        other.Headers.Add("X-Forwarded-For", "203.0.113.8");
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, other));
    }

    [Fact]
    public async Task A_forwarded_address_from_a_source_that_is_not_trusted_is_ignored()
    {
        // Otherwise anyone could pick a new address with every request and never reach the limit.
        var client = Start(maxFailures: 2, extra: ("Security:TrustedProxyNetworks:0", "10.0.0.0/8"));

        for (var i = 0; i < 2; i++)
        {
            var failure = Get("/endpoints", $"Bearer {BadKey}", from: "198.51.100.9");
            failure.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            await client.SendAsync(failure);
        }

        var next = Get("/endpoints", $"Bearer {BadKey}", from: "198.51.100.9");
        next.Headers.Add("X-Forwarded-For", "203.0.113.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, next));
    }

    [Fact]
    public async Task Without_trusted_proxies_a_forwarded_address_is_never_believed()
    {
        var client = Start(maxFailures: 2);

        for (var i = 0; i < 2; i++)
        {
            var failure = Get("/endpoints", $"Bearer {BadKey}", from: "10.0.0.5");
            failure.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            await client.SendAsync(failure);
        }

        var next = Get("/endpoints", $"Bearer {BadKey}", from: "10.0.0.5");
        next.Headers.Add("X-Forwarded-For", "203.0.113.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, next));
    }

    // ---------- settings ----------

    [Fact]
    public void A_ceiling_of_no_clients_stops_the_service()
    {
        using var factory = new ApiFactory(
            Postgres.ConnectionString,
            AdminKey,
            new Dictionary<string, string?> { ["Security:AuthFailureLimit:MaxTrackedClients"] = "0" });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void A_limit_below_one_stops_the_service_instead_of_switching_the_protection_off(string maxFailures)
    {
        using var factory = new ApiFactory(
            Postgres.ConnectionString,
            AdminKey,
            new Dictionary<string, string?> { ["Security:AuthFailureLimit:MaxFailures"] = maxFailures });

        // The settings are read when the service starts, so starting it is what fails.
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/x")]
    [InlineData("2001:db8::/129")]
    [InlineData("")]
    public void A_proxy_network_that_is_not_a_network_stops_the_service_instead_of_trusting_the_wrong_thing(string network)
    {
        using var factory = new ApiFactory(
            Postgres.ConnectionString,
            AdminKey,
            new Dictionary<string, string?> { ["Security:TrustedProxyNetworks:0"] = network });

        // The message says what is wrong and which setting, not just that something failed.
        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Security:TrustedProxyNetworks", failure.ToString());
        Assert.Contains("is not a network", failure.ToString());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Only_the_networks_that_are_listed_are_trusted_not_the_machine_itself(string remote)
    {
        // The framework trusts loopback by default. A list that is configured replaces that, it does not add to it.
        var client = Start(maxFailures: 2, extra: ("Security:TrustedProxyNetworks:0", "10.0.0.0/8"));

        for (var i = 0; i < 2; i++)
        {
            var failure = Get("/endpoints", $"Bearer {BadKey}", from: remote);
            failure.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            await client.SendAsync(failure);
        }

        var next = Get("/endpoints", $"Bearer {BadKey}", from: remote);
        next.Headers.Add("X-Forwarded-For", "203.0.113.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, next));
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.168.1.0/24")]
    [InlineData("203.0.113.7/32")]
    [InlineData("2001:db8::/32")]
    [InlineData("0.0.0.0/0")]
    public void A_proxy_network_that_is_one_is_accepted(string network)
    {
        using var factory = new ApiFactory(
            Postgres.ConnectionString,
            AdminKey,
            new Dictionary<string, string?> { ["Security:TrustedProxyNetworks:0"] = network });

        using var client = factory.CreateClient();
        Assert.NotNull(client);
    }

    [Fact]
    public async Task Failures_are_logged_once_when_a_client_is_blocked_without_the_key()
    {
        // Covered by the unit tests of the limiter (only one failure reports reaching the limit); here it is only
        // checked that the key never ends up in a response.
        var client = Start(maxFailures: 1);

        var first = await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));
        var second = await client.SendAsync(Get("/endpoints", $"Bearer {BadKey}"));

        Assert.DoesNotContain(BadKey, await first.Content.ReadAsStringAsync());
        Assert.DoesNotContain(BadKey, await second.Content.ReadAsStringAsync());
    }
}
