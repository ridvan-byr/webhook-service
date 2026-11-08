using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using WebhookService.Core.Signing;

namespace WebhookService.Infrastructure.Dispatch;

public sealed record SendResult(int? Status, string? ResponseSnippet, string? Error, long DurationMs)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>Sends one signed HTTP request. It never throws for network problems; it reports them.</summary>
public sealed class WebhookSender
{
    private const int MaxResponseBytes = 2048;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public WebhookSender(HttpClient http, TimeProvider time, IOptions<DeliveryOptions> options)
    {
        _http = http;
        _time = time;
        _timeout = options.Value.RequestTimeout;
    }

    public async Task<SendResult> SendAsync(
        Uri url,
        string signingSecret,
        Guid messageId,
        string payloadJson,
        CancellationToken ct)
    {
        // Sign and send exactly the same bytes.
        var body = Encoding.UTF8.GetBytes(payloadJson);
        var id = messageId.ToString();
        var timestamp = _time.GetUtcNow().ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("Webhook-Id", id);
        request.Headers.Add("Webhook-Timestamp", timestamp.ToString());
        request.Headers.Add("Webhook-Signature", WebhookSigner.Sign(signingSecret, id, timestamp, body));
        request.Headers.UserAgent.ParseAdd("WebhookService/0.1");

        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);

            var snippet = await ReadSnippetAsync(response, timeoutSource.Token);
            return new SendResult((int)response.StatusCode, snippet, null, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout fired (not a shutdown request).
            return new SendResult(null, null, $"Timed out after {_timeout.TotalSeconds:0.#} s.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new SendResult(null, null, $"{ex.GetType().Name}: {ex.Message}", stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task<string?> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Read only the beginning of the body: a hostile receiver could stream gigabytes.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }
}
