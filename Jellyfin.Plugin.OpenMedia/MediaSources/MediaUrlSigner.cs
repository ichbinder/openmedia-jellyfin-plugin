using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.OpenMedia.MediaSources;

/// <summary>
/// Signs media stream URLs with HMAC-SHA256 for stateless URL authentication.
/// Produces ?sig=&lt;hmac&gt;&amp;exp=&lt;unix_ts&gt;&amp;u=&lt;userId&gt; query parameters
/// compatible with the openmedia-api <c>verifyMediaUrl</c> verifier.
/// </summary>
public static class MediaUrlSigner
{
    /// <summary>Default TTL: 6 hours.</summary>
    public const int DefaultTtlSeconds = 21_600;

    /// <summary>Maximum allowed TTL — enforced at sign time.</summary>
    public const int MaxTtlSeconds = 21_600;

    /// <summary>Minimum HMAC secret length to prevent weak keys.</summary>
    public const int MinSecretLength = 32;

    /// <summary>
    /// Sign a media URL with an HMAC-SHA256 signature.
    /// <para>
    /// Computation (identical to TS signer in openmedia-api):
    /// <list type="number">
    ///   <item>exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds</item>
    ///   <item>payload = $"{hash}.{exp}.{userId}"</item>
    ///   <item>sig = Base64Url(HMACSHA256(secret, payload)) — no padding</item>
    ///   <item>return $"{apiUrl}/jellyfin/stream/{hash}?sig={sig}&amp;exp={exp}&amp;u={userId}"</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="secret">HMAC shared secret (min 32 chars).</param>
    /// <param name="apiUrl">Base URL of the openmedia-API.</param>
    /// <param name="hash">SHA-256 hash of the media item.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="ttlSeconds">TTL in seconds (default 21600 = 6h).</param>
    /// <returns>Signed URL with sig, exp, u query parameters.</returns>
    /// <exception cref="InvalidOperationException">Thrown when secret is empty or too short.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when ttlSeconds exceeds MaxTtlSeconds.</exception>
    public static string SignMediaUrl(
        string secret,
        string apiUrl,
        string hash,
        string userId,
        int ttlSeconds = DefaultTtlSeconds)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "MediaSigningSecret is not configured. Cannot sign media URLs.");
        }

        if (secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException(
                $"HMAC secret must be at least {MinSecretLength} characters, got {secret.Length}.");
        }

        if (ttlSeconds > MaxTtlSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttlSeconds),
                $"TTL must not exceed {MaxTtlSeconds} seconds (6h), got {ttlSeconds}.");
        }

        if (ttlSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttlSeconds),
                $"TTL must be positive, got {ttlSeconds}.");
        }

        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;
        var payload = $"{hash}.{exp}.{userId}";

        // HMAC-SHA256 → Base64Url without padding
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac = HMACSHA256.HashData(keyBytes, payloadBytes);
        var sig = Convert.ToBase64String(hmac)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var baseUrl = apiUrl.TrimEnd('/');
        return $"{baseUrl}/jellyfin/stream/{hash}?sig={sig}&exp={exp}&u={Uri.EscapeDataString(userId)}";
    }

    /// <summary>
    /// Sign a media URL using fixed timestamp (for deterministic testing).
    /// Identical to <see cref="SignMediaUrl"/> but accepts an explicit unix timestamp
    /// instead of using the current clock.
    /// </summary>
    internal static string SignMediaUrlWithTimestamp(
        string secret,
        string apiUrl,
        string hash,
        string userId,
        long fixedExp,
        int ttlSeconds = DefaultTtlSeconds)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "MediaSigningSecret is not configured. Cannot sign media URLs.");
        }

        if (secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException(
                $"HMAC secret must be at least {MinSecretLength} characters, got {secret.Length}.");
        }

        var payload = $"{hash}.{fixedExp}.{userId}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac = HMACSHA256.HashData(keyBytes, payloadBytes);
        var sig = Convert.ToBase64String(hmac)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var baseUrl = apiUrl.TrimEnd('/');
        return $"{baseUrl}/jellyfin/stream/{hash}?sig={sig}&exp={fixedExp}&u={Uri.EscapeDataString(userId)}";
    }
}
