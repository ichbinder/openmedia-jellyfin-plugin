using System;
using System.Net;
using Jellyfin.Plugin.OpenMedia.MediaSources;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class MediaUrlSignerTests
{
    // 32-char secret (minimum length)
    private const string ValidSecret = "abcdefghijklmnopqrstuvwxyz123456";
    private const string ApiUrl = "https://api.mediatoken.de";
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string UserId = "user42";

    [Fact]
    public void SignMediaUrl_WithValidInputs_ReturnsExpectedPath()
    {
        var url = MediaUrlSigner.SignMediaUrl(ValidSecret, ApiUrl, Hash, UserId);

        Assert.StartsWith($"{ApiUrl}/jellyfin/stream/{Hash}?", url);
        Assert.Contains("sig=", url);
        Assert.Contains("&exp=", url);
        Assert.Contains("&u=", url);
    }

    [Fact]
    public void SignMediaUrl_ProducesBase64UrlWithoutPadding()
    {
        var url = MediaUrlSigner.SignMediaUrl(ValidSecret, ApiUrl, Hash, UserId);

        // Extract sig value from URL
        var sigStart = url.IndexOf("sig=", StringComparison.Ordinal) + 4;
        var sigEnd = url.IndexOf('&', sigStart);
        var sig = url[sigStart..sigEnd];

        // Base64Url: no +, no /, no =
        Assert.DoesNotContain('+', sig);
        Assert.DoesNotContain('/', sig);
        Assert.DoesNotContain('=', sig);
    }

    [Fact]
    public void SignMediaUrl_Deterministic_WithSameInputs()
    {
        // Using internal method with fixed timestamp for deterministic testing
        const long fixedExp = 1_700_000_000;

        var url1 = MediaUrlSigner.SignMediaUrlWithTimestamp(ValidSecret, ApiUrl, Hash, UserId, fixedExp);
        var url2 = MediaUrlSigner.SignMediaUrlWithTimestamp(ValidSecret, ApiUrl, Hash, UserId, fixedExp);

        Assert.Equal(url1, url2);
    }

    [Fact]
    public void SignMediaUrl_Throws_OnEmptySecret()
    {
        Assert.Throws<InvalidOperationException>(
            () => MediaUrlSigner.SignMediaUrl(string.Empty, ApiUrl, Hash, UserId));
    }

    [Fact]
    public void SignMediaUrl_Throws_OnTooShortSecret()
    {
        // 31 chars — one below minimum
        var shortSecret = new string('a', 31);

        var ex = Assert.Throws<InvalidOperationException>(
            () => MediaUrlSigner.SignMediaUrl(shortSecret, ApiUrl, Hash, UserId));

        Assert.Contains("at least 32", ex.Message);
    }

    [Fact]
    public void SignMediaUrl_Throws_OnTtlExceeding6Hours()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaUrlSigner.SignMediaUrl(ValidSecret, ApiUrl, Hash, UserId, ttlSeconds: 21601));

        Assert.Contains("must not exceed", ex.Message);
    }

    [Fact]
    public void SignMediaUrl_Throws_OnZeroTtl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaUrlSigner.SignMediaUrl(ValidSecret, ApiUrl, Hash, UserId, ttlSeconds: 0));
    }

    [Fact]
    public void SignMediaUrl_ContainsCorrectExpValue()
    {
        // Use fixed timestamp to verify exp is in the URL
        const long fixedExp = 1_700_000_000;

        var url = MediaUrlSigner.SignMediaUrlWithTimestamp(ValidSecret, ApiUrl, Hash, UserId, fixedExp);

        Assert.Contains($"exp={fixedExp}", url);
    }

    [Fact]
    public void SignMediaUrl_ContainsCorrectUserId()
    {
        var url = MediaUrlSigner.SignMediaUrl(ValidSecret, ApiUrl, Hash, UserId);

        Assert.Contains($"u={WebUtility.UrlEncode(UserId)}", url);
    }

    [Fact]
    public void SignMediaUrl_ContainsCorrectHash()
    {
        var url = MediaUrlSigner.SignMediaUrl(ValidSecret, ApiUrl, Hash, UserId);

        Assert.Contains($"/jellyfin/stream/{Hash}", url);
    }

    [Fact]
    public void SignMediaUrl_DifferentSecrets_ProduceDifferentSigs()
    {
        const long fixedExp = 1_700_000_000;
        var otherSecret = "zyxwvutsrqponmlkjihgfedcba654321"; // 32 chars

        var url1 = MediaUrlSigner.SignMediaUrlWithTimestamp(ValidSecret, ApiUrl, Hash, UserId, fixedExp);
        var url2 = MediaUrlSigner.SignMediaUrlWithTimestamp(otherSecret, ApiUrl, Hash, UserId, fixedExp);

        Assert.NotEqual(url1, url2);
    }

    [Fact]
    public void SignMediaUrl_Throws_OnNullSecret()
    {
        Assert.Throws<InvalidOperationException>(
            () => MediaUrlSigner.SignMediaUrl(null!, ApiUrl, Hash, UserId));
    }
}
