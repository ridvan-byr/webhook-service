using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookService.Core.Signing;

/// <summary>
/// Signs and verifies webhook requests with HMAC-SHA256.
///
/// The signed text is "{messageId}.{timestamp}.{body}". Including the id and timestamp stops an
/// attacker from replaying an old request or re-using a signature on a different message.
/// The signature header looks like "v1,{base64}". Several signatures may be listed separated by
/// spaces, which allows secrets to be rotated without downtime.
///
/// The format is modelled on the Standard Webhooks convention (from memory, not checked against
/// the specification), so receivers can use a ready-made verifier library only after that
/// compatibility has been verified.
/// </summary>
public static class WebhookSigner
{
    public const string SecretPrefix = "whsec_";
    public const string Version = "v1";

    public static string Sign(string secret, string messageId, long unixSeconds, ReadOnlySpan<byte> body)
    {
        var key = KeyBytes(secret);
        var prefix = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{messageId}.{unixSeconds}."));

        var signedContent = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedContent, 0);
        body.CopyTo(signedContent.AsSpan(prefix.Length));

        var hash = HMACSHA256.HashData(key, signedContent);
        return $"{Version},{Convert.ToBase64String(hash)}";
    }

    /// <summary>Checks a request the way a receiver should: signature first, then freshness.</summary>
    public static bool Verify(
        string secret,
        string messageId,
        string timestampHeader,
        string signatureHeader,
        ReadOnlySpan<byte> body,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (age.Duration() > tolerance)
        {
            return false;
        }

        var expected = Sign(secret, messageId, unixSeconds, body);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        var matched = false;
        foreach (var candidate in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Compare in constant time so the comparison does not leak how many characters matched.
            // No early exit: every candidate is checked.
            var candidateBytes = Encoding.UTF8.GetBytes(candidate);
            matched |= CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
        }

        return matched;
    }

    private static byte[] KeyBytes(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Secret is required.", nameof(secret));
        }

        return secret.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? Convert.FromBase64String(secret[SecretPrefix.Length..])
            : Encoding.UTF8.GetBytes(secret);
    }
}
