using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace WebhookService.Tests.Dispatch;

public sealed record ReceivedRequest(string Path, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// A tiny real HTTP server on a random local port that plays the part of a webhook receiver.
///   /ok       answers 200
///   /fail     answers 500 with a body
///   /slow     waits up to 30 s (or until the caller gives up)
///   /hold     answers 200 after 0.4 s and remembers how many requests were waiting at once
///   /redirect answers 302 to /ok
/// </summary>
public sealed class TestReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestReceiver(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    private int _inFlight;
    private int _maxInFlight;

    /// <summary>The most requests that were being answered at the same moment, counted on /hold.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxInFlight);

    private void Enter()
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
        {
        }
    }

    private void Leave() => Interlocked.Decrement(ref _inFlight);

    public static async Task<TestReceiver> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        TestReceiver? receiver = null;

        app.Map("/{**path}", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            receiver!.Requests.Enqueue(new ReceivedRequest(context.Request.Path.Value!, headers, body));

            switch (context.Request.Path.Value)
            {
                case "/fail":
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync("boom");
                    break;
                case "/slow":
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                    break;
                case "/hold":
                    // Answers after a moment, so requests that are sent together overlap and can be counted.
                    receiver!.Enter();
                    try
                    {
                        await Task.Delay(400, context.RequestAborted);
                        context.Response.StatusCode = 200;
                    }
                    finally
                    {
                        receiver.Leave();
                    }

                    break;
                case "/redirect":
                    context.Response.StatusCode = 302;
                    context.Response.Headers.Location = "/ok";
                    break;
                default:
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync("ok");
                    break;
            }
        });

        await app.StartAsync();
        var address = app.Urls.First();
        receiver = new TestReceiver(app, address.TrimEnd('/'));
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
