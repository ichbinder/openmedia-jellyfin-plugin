using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.MediaSources;

/// <summary>
/// Registriert pro Library-Item ({hash}.strm) eine zusaetzliche Protocol=Http-MediaSource.
/// Ermöglicht nativen Download in Jellyfin-Clients (Swiftfin, Jellyfin Media Player).
/// Failure-Fallback: bei API-Down/Fehler → leere Liste, .strm-Source bleibt nutzbar.
/// </summary>
public class OpenMediaMediaSourceProvider : IMediaSourceProvider
{
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private const string LibraryCacheKey = "openmedia:mediasource:library";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly ILogger<OpenMediaMediaSourceProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;

    public OpenMediaMediaSourceProvider(
        ILogger<OpenMediaMediaSourceProvider> logger,
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;

        // Config-Check — nicht konfiguriert → stillschweigend leer
        if (config is null || string.IsNullOrWhiteSpace(config.ApiToken))
        {
            _logger.LogDebug("provider:fallback_empty reason=not_configured itemId={ItemId}", item.Id);
            return Enumerable.Empty<MediaSourceInfo>();
        }

        // Non-.strm-Pfade → nicht zuständig
        if (item.Path is null || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("provider:fallback_empty reason=not_strm itemId={ItemId} path={Path}", item.Id, item.Path);
            return Enumerable.Empty<MediaSourceInfo>();
        }

        // .strm aber ungültiger Hash-Stem
        var hash = ExtractHash(item.Path);
        if (hash is null)
        {
            _logger.LogDebug("provider:fallback_empty reason=hash_invalid itemId={ItemId} path={Path}", item.Id, item.Path);
            return Enumerable.Empty<MediaSourceInfo>();
        }

        _logger.LogInformation(
            "provider:invoked hash={Hash} itemId={ItemId}",
            hash,
            item.Id);

        try
        {
            // Library aus Cache oder API holen
            var library = await GetCachedLibraryAsync(config, cancellationToken).ConfigureAwait(false);

            // Hash in Library suchen
            var libraryItem = library.FirstOrDefault(li => string.Equals(li.Hash, hash, StringComparison.Ordinal));
            if (libraryItem is null)
            {
                _logger.LogWarning(
                    "provider:fallback_empty reason=item_missing hash={Hash} itemId={ItemId}",
                    hash,
                    item.Id);
                return Enumerable.Empty<MediaSourceInfo>();
            }

            // FileSize parsen
            long? sizeBytes = null;
            if (!string.IsNullOrWhiteSpace(libraryItem.FileSize) && long.TryParse(libraryItem.FileSize, out var parsedSize))
            {
                sizeBytes = parsedSize;
            }

            // Signed URL bauen (HMAC-SHA256), fallback auf ?token= wenn Secret fehlt
            var streamUrl = BuildStreamUrl(config, hash);

            var mediaSource = new MediaSourceInfo
            {
                Id = $"openmedia-http-{hash}",
                Name = "Download (Original)",
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                Container = "mp4",
                Size = sizeBytes,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                Type = MediaSourceType.Default
            };

            _logger.LogInformation(
                "provider:returned hash={Hash} mediaSourceCount=1 sizeBytes={SizeBytes} itemId={ItemId}",
                hash,
                sizeBytes,
                item.Id);

            return new[] { mediaSource };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "provider:fallback_empty reason=api_error hash={Hash} itemId={ItemId}",
                hash,
                item.Id);
            return Enumerable.Empty<MediaSourceInfo>();
        }
    }

    /// <summary>
    /// Builds the stream URL. Tries HMAC-signed URL first; falls back to ?token=
    /// when MediaSigningSecret is not configured.
    /// </summary>
    private string BuildStreamUrl(PluginConfiguration config, string hash)
    {
        var apiUrl = config.ApiUrl.TrimEnd('/');

        // Try signed URL if secret is configured
        if (!string.IsNullOrWhiteSpace(config.MediaSigningSecret))
        {
            var userId = ResolveUserId(config);

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning(
                    "provider:signed_url_skipped reason=no_userId hash={Hash}",
                    hash);
            }
            else
            {
                try
                {
                    var signedUrl = MediaUrlSigner.SignMediaUrl(
                        config.MediaSigningSecret,
                        apiUrl,
                        hash,
                        userId);

                    // Extract exp from URL for logging
                    var expStart = signedUrl.IndexOf("&exp=", StringComparison.Ordinal);
                    var expValue = expStart >= 0
                        ? signedUrl[(expStart + 5)..].Split('&')[0]
                        : "?";
                    var expInSec = long.TryParse(expValue, out var expTs)
                        ? expTs - DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        : 0;
                    var sigPrefix = signedUrl.Contains("sig=")
                        ? signedUrl.Substring(signedUrl.IndexOf("sig=", StringComparison.Ordinal) + 4, Math.Min(8, signedUrl.Length - signedUrl.IndexOf("sig=", StringComparison.Ordinal) - 4))
                        : "?";

                    _logger.LogInformation(
                        "provider:signed_url hash={Hash} userId={UserId} exp_in_sec={ExpInSec} sig={SigPrefix}",
                        hash,
                        userId,
                        expInSec,
                        sigPrefix);

                    return signedUrl;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "provider:fallback_token reason=sign_error hash={Hash}",
                        hash);
                    // Fall through to token fallback
                }
            }
        }

        // Fallback: legacy ?token= mode (R058 backward compat)
        var encodedToken = Uri.EscapeDataString(config.ApiToken);
        _logger.LogInformation(
            "provider:token_url hash={Hash} reason=no_secret_or_sign_failed",
            hash);
        return $"{apiUrl}/jellyfin/stream/{hash}?token={encodedToken}";
    }

    /// <summary>
    /// Resolves the userId for signing. Uses DefaultUserId from config.
    /// IMediaSourceProvider.GetMediaSources has no direct user context.
    /// </summary>
    private static string ResolveUserId(PluginConfiguration config)
    {
        return config.DefaultUserId ?? string.Empty;
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(
        string openToken,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "OpenMediaMediaSourceProvider provides direct HTTP URLs and does not require OpenMediaSource.");
    }

    /// <summary>
    /// Holt die Library aus dem 5-Min-Memory-Cache oder fragt die API ab.
    /// </summary>
    private async Task<IReadOnlyList<LibraryItem>> GetCachedLibraryAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(LibraryCacheKey, out IReadOnlyList<LibraryItem>? cached) && cached is not null)
        {
            // Cache-Alter für structured logging abschätzen (nicht exakt, aber nützlich)
            _logger.LogDebug("provider:cache_hit key={Key}", LibraryCacheKey);
            return cached;
        }

        var http = _httpClientFactory.CreateClient(nameof(OpenMediaApiClient));
        var client = new OpenMediaApiClient(http, config.ApiUrl, config.ApiToken);

        var library = await client.GetLibraryAsync(cancellationToken).ConfigureAwait(false);

        _memoryCache.Set(LibraryCacheKey, library, CacheDuration);

        _logger.LogInformation(
            "provider:library_fetched itemCount={Count} cacheDurationMinutes={Minutes}",
            library.Count,
            CacheDuration.TotalMinutes);

        return library;
    }

    /// <summary>
    /// Extrahiert den SHA-256-Hash aus dem Dateinamen einer .strm-Datei.
    /// Erwartet: /path/to/{64-hex-chars}.strm
    /// Gibt null zurück wenn der Pfad null ist, keine .strm-Datei, oder der Hash nicht dem Pattern entspricht.
    /// </summary>
    internal static string? ExtractHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(fileName) || !HashPattern.IsMatch(fileName))
        {
            return null;
        }

        return fileName;
    }
}
