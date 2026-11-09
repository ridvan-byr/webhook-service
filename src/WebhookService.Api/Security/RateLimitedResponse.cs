using Microsoft.AspNetCore.Mvc;

namespace WebhookService.Api.Security;

/// <summary>The answer to a client that has made too many failed attempts to authenticate.</summary>
internal static class RateLimitedResponse
{
    public static Task WriteAsync(HttpResponse response, TimeSpan retryAfter)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Only how long to wait. Nothing about which key or which attempt, which could help someone guess.
        return response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many failed attempts to authenticate. Try again later.",
            },
            options: null,
            contentType: "application/problem+json");
    }
}
