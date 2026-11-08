using System.Security.Cryptography;
using System.Text;

namespace WebhookService.Core.Security;

/// <summary>
/// Creates and checks API keys.
///
/// A key looks like "whk_{prefix}_{secret}". The 12-character hex prefix is not secret: it is
/// stored in clear and only used to find the right database row quickly. The secret part is
/// 32 random bytes. Only a SHA-256 hash of the whole key is stored, so a leaked database does
/// not reveal usable keys.
///
/// Plain SHA-256 is enough here (no slow password hash needed) because the keys are long and
/// random. Slow hashes protect low-entropy secrets such as human-chosen passwords.
/// </summary>
public static class ApiKeyGenerator
{
    public const string KeyPrefix = "whk_";
    public const int IdLength = 12;

    // "whk_" + 12 hex characters + "_" => the secret starts at this index.
    private const int SecretStart = 4 + IdLength + 1;

    public sealed record Generated(string PlainText, string Prefix, byte[] Hash);

    public static Generated Generate()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(IdLength / 2)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var plainText = $"{KeyPrefix}{prefix}_{secret}";

        return new Generated(plainText, prefix, Hash(plainText));
    }

    public static byte[] Hash(string plainText) => SHA256.HashData(Encoding.UTF8.GetBytes(plainText));

    /// <summary>Reads the lookup prefix from a key. Returns false if the text is not shaped like a key.</summary>
    public static bool TryGetPrefix(string? plainText, out string prefix)
    {
        prefix = string.Empty;

        if (plainText is null
            || plainText.Length <= SecretStart
            || !plainText.StartsWith(KeyPrefix, StringComparison.Ordinal)
            || plainText[SecretStart - 1] != '_')
        {
            return false;
        }

        var candidate = plainText.Substring(KeyPrefix.Length, IdLength);
        if (!candidate.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            return false;
        }

        prefix = candidate;
        return true;
    }

    /// <summary>Checks a presented key against a stored hash in constant time.</summary>
    public static bool Matches(string plainText, byte[] storedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(plainText), storedHash);

    /// <summary>
    /// Compares two secrets in constant time. Both are hashed first, so the comparison also
    /// does not reveal how long the real secret is.
    /// </summary>
    public static bool SecretsEqual(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
