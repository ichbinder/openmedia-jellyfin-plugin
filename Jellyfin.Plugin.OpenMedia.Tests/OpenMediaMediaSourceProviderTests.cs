using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Configuration;
using Jellyfin.Plugin.OpenMedia.MediaSources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

/// <summary>
/// xUnit-Tests für OpenMediaMediaSourceProvider:
/// Provider-Shape, Hash-Validation, Cache-Hit, API-Error-Fallback.
/// Validiert alle 5 Fallback-Reasons + Happy-Path + Cache-Hit (R058/R059).
/// </summary>
public sealed class OpenMediaMediaSourceProviderTests : IDisposable
{
    private const string ValidHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ApiUrl = "https://api.test.com";
    private const string ApiToken = "om_test_token_12345";
    private const string TestSecret = "test-secret-that-is-at-least-32-characters-long";
    private const string TestUserId = "user-123";

    private static readonly JsonSerializerOptions JsonCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly CaptureLogger<OpenMediaMediaSourceProvider> _logger;
    private readonly MemoryCache _memoryCache;
    private readonly string _tempDir;

    public OpenMediaMediaSourceProviderTests()
    {
        _logger = new();
        _memoryCache = new(new MemoryCacheOptions());
        _tempDir = Path.Combine(Path.GetTempPath(), $"om-provider-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        SetupPlugin(ApiUrl, ApiToken);
    }

    public void Dispose()
    {
        _memoryCache.Dispose();
        ResetPluginInstance();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // ─── Test 1: item.Path=null → leere Liste + fallback_empty(not_strm) ───

    [Fact]
    public async Task NullPath_ReturnsEmpty_NotStrm()
    {
        var provider = CreateProvider();
        var item = new Video { Id = Guid.NewGuid() };

        var result = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.Empty(result);
        Assert.Contains(_logger.Messages, m => m.Contains("not_strm"));
    }

    // ─── Test 2: nicht-64-hex-Stem → fallback_empty(hash_invalid) ───

    [Fact]
    public async Task NonHashStrmFile_ReturnsEmpty_HashInvalid()
    {
        var provider = CreateProvider();
        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = "/media/invalid-name.strm"
        };

        var result = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.Empty(result);
        Assert.Contains(_logger.Messages, m => m.Contains("hash_invalid"));
    }

    // ─── Test 3: Happy-Path — gültiger hash + signed URL (sig, exp, u query params) ───

    [Fact]
    public async Task ValidHashWithLibraryItem_ReturnsMediaSource_WithSignedUrl()
    {
        // Setup plugin with signing secret + default user
        ResetPluginInstance();
        SetupPlugin(ApiUrl, ApiToken, TestSecret, TestUserId);

        var handler = JsonHandler(new
        {
            items = new object[]
            {
                new { hash = ValidHash, tmdbId = 1234L, title = "Test Movie", year = 2024, fileSize = "1500000000", duration = 120, resolution = "1080p" }
            }
        });
        var provider = CreateProvider(handler);

        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = $"/media/{ValidHash}.strm"
        };

        var result = await provider.GetMediaSources(item, CancellationToken.None);
        var list = result.ToList();

        Assert.Single(list);
        var source = list[0];

        // Shape assertions
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.True(source.IsRemote);
        Assert.Equal(1500000000L, source.Size);
        Assert.Equal("mp4", source.Container);
        Assert.Equal("Download (Original)", source.Name);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.False(source.SupportsTranscoding);

        // Path format: signed URL with sig, exp, u query params — no ?token=
        Assert.Contains("?sig=", source.Path);
        Assert.Contains("&exp=", source.Path);
        Assert.Contains("&u=", source.Path);
        Assert.DoesNotContain("?token=", source.Path);
        Assert.StartsWith($"{ApiUrl}/jellyfin/stream/{ValidHash}", source.Path);

        // Structured logging
        Assert.Contains(_logger.Messages, m => m.Contains("provider:invoked"));
        Assert.Contains(_logger.Messages, m => m.Contains("provider:returned"));
        Assert.Contains(_logger.Messages, m => m.Contains("provider:signed_url"));
    }

    // ─── Test 4: ApiClient wirft HttpRequestException → fallback_empty(api_error) ───

    [Fact]
    public async Task ApiThrows_ReturnsEmpty_ApiError()
    {
        var handler = ErrorHandler();
        var provider = CreateProvider(handler);

        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = $"/media/{ValidHash}.strm"
        };

        var result = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.Empty(result);
        Assert.Contains(_logger.Messages, m => m.Contains("api_error"));
    }

    // ─── Test 5: Zweiter Aufruf innerhalb 5min → ApiClient nicht erneut aufgerufen (Cache-Hit) ───

    [Fact]
    public async Task SecondCallWithin5Min_UsesCache_NoSecondApiCall()
    {
        var handler = JsonHandler(new
        {
            items = new object[]
            {
                new { hash = ValidHash, tmdbId = 1234L, title = "Test", year = 2024, fileSize = "1000", duration = 120, resolution = "1080p" }
            }
        });
        var provider = CreateProvider(handler);

        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = $"/media/{ValidHash}.strm"
        };

        // First call — populates cache
        var first = await provider.GetMediaSources(item, CancellationToken.None);
        Assert.Single(first);
        Assert.Equal(1, handler.CallCount);

        // Second call — should hit cache
        var second = await provider.GetMediaSources(item, CancellationToken.None);
        Assert.Single(second);
        Assert.Equal(1, handler.CallCount); // No additional API call

        // Cache-hit logging
        Assert.Contains(_logger.Messages, m => m.Contains("cache_hit"));
    }

    // ─── Test 6: leerer ApiToken → fallback_empty(not_configured), kein ApiClient-Call ───

    [Fact]
    public async Task EmptyApiToken_ReturnsEmpty_NotConfigured()
    {
        ResetPluginInstance();
        SetupPlugin(ApiUrl, ""); // Empty token

        var handler = ErrorHandler(); // Should never be called
        var provider = CreateProvider(handler);

        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = $"/media/{ValidHash}.strm"
        };

        var result = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount); // No HTTP call attempted
        Assert.Contains(_logger.Messages, m => m.Contains("not_configured"));
    }

    // ─── Test 7: hash nicht in Library-Response → fallback_empty(item_missing) ───

    [Fact]
    public async Task HashNotInLibrary_ReturnsEmpty_ItemMissing()
    {
        var handler = JsonHandler(new
        {
            items = new object[]
            {
                new { hash = OtherHash, tmdbId = 5678L, title = "Other Movie", year = 2023, fileSize = "2000", duration = 90, resolution = "720p" }
            }
        });
        var provider = CreateProvider(handler);

        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = $"/media/{ValidHash}.strm"
        };

        var result = await provider.GetMediaSources(item, CancellationToken.None);

        Assert.Empty(result);
        Assert.Contains(_logger.Messages, m => m.Contains("item_missing"));
    }

    // ─── Test 8: MediaSigningSecret leer → fallback auf ?token= Modus ───

    [Fact]
    public async Task EmptySigningSecret_FallsBackToTokenMode()
    {
        // Plugin without signing secret (default setup — no secret, no userId)
        var handler = JsonHandler(new
        {
            items = new object[]
            {
                new { hash = ValidHash, tmdbId = 1234L, title = "Test", year = 2024, fileSize = "5000", duration = 120, resolution = "1080p" }
            }
        });
        var provider = CreateProvider(handler);

        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = $"/media/{ValidHash}.strm"
        };

        var result = await provider.GetMediaSources(item, CancellationToken.None);
        var list = result.ToList();

        Assert.Single(list);
        var source = list[0];

        // Falls back to ?token= mode
        Assert.Equal($"{ApiUrl}/jellyfin/stream/{ValidHash}?token={Uri.EscapeDataString(ApiToken)}", source.Path);
        Assert.Contains(_logger.Messages, m => m.Contains("provider:token_url"));
    }

    // ─── Test 9: SignMediaUrl wirft → fallback auf ?token= + LogError ───

    [Fact]
    public async Task SignMediaUrlThrows_FallsBackToTokenMode_LogsError()
    {
        // Setup with a secret that's too short — will throw InvalidOperationException
        ResetPluginInstance();
        SetupPlugin(ApiUrl, ApiToken, "short", TestUserId);

        var handler = JsonHandler(new
        {
            items = new object[]
            {
                new { hash = ValidHash, tmdbId = 1234L, title = "Test", year = 2024, fileSize = "5000", duration = 120, resolution = "1080p" }
            }
        });
        var provider = CreateProvider(handler);

        var item = new Video
        {
            Id = Guid.NewGuid(),
            Path = $"/media/{ValidHash}.strm"
        };

        var result = await provider.GetMediaSources(item, CancellationToken.None);
        var list = result.ToList();

        Assert.Single(list);
        var source = list[0];

        // Falls back to ?token= mode despite signing error
        Assert.Equal($"{ApiUrl}/jellyfin/stream/{ValidHash}?token={Uri.EscapeDataString(ApiToken)}", source.Path);
        Assert.Contains(_logger.Messages, m => m.Contains("provider:fallback_token reason=sign_error"));
    }

    // ─── Bonus: ExtractHash unit tests (internal static, accessible via InternalsVisibleTo) ───

    [Fact]
    public void ExtractHash_NullPath_ReturnsNull()
    {
        Assert.Null(OpenMediaMediaSourceProvider.ExtractHash(null));
    }

    [Fact]
    public void ExtractHash_EmptyPath_ReturnsNull()
    {
        Assert.Null(OpenMediaMediaSourceProvider.ExtractHash(""));
    }

    [Fact]
    public void ExtractHash_NonStrmExtension_ReturnsNull()
    {
        Assert.Null(OpenMediaMediaSourceProvider.ExtractHash("/media/movie.mp4"));
    }

    [Fact]
    public void ExtractHash_InvalidHashStem_ReturnsNull()
    {
        Assert.Null(OpenMediaMediaSourceProvider.ExtractHash("/media/invalid-name.strm"));
    }

    [Fact]
    public void ExtractHash_ValidHashStem_ReturnsHash()
    {
        Assert.Equal(ValidHash, OpenMediaMediaSourceProvider.ExtractHash($"/media/{ValidHash}.strm"));
    }

    [Fact]
    public void ExtractHash_UpperCaseStrmExtension_ReturnsHash()
    {
        Assert.Equal(ValidHash, OpenMediaMediaSourceProvider.ExtractHash($"/media/{ValidHash}.STRM"));
    }

    [Fact]
    public void ExtractHash_UpperCaseHex_ReturnsNull()
    {
        var upper = ValidHash.ToUpperInvariant();
        Assert.Null(OpenMediaMediaSourceProvider.ExtractHash($"/media/{upper}.strm"));
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers & Fakes
    // ═══════════════════════════════════════════════════════════════

    private OpenMediaMediaSourceProvider CreateProvider(StubHttpHandler? handler = null)
    {
        handler ??= JsonHandler(new { items = Array.Empty<object>() });
        var factory = new StubHttpClientFactory(handler);
        return new OpenMediaMediaSourceProvider(_logger, factory, _memoryCache);
    }

    private static StubHttpHandler JsonHandler(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonCamelCase);
        return new((_, _) =>
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
    }

    private static StubHttpHandler ErrorHandler()
        => new((_, _) => throw new HttpRequestException("API is down"));

    /// <summary>
    /// Constructs a real Plugin instance with minimal fakes so Plugin.Instance.Configuration is available.
    /// </summary>
    private void SetupPlugin(string apiUrl, string apiToken, string? signingSecret = null, string? defaultUserId = null)
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var paths = new StubAppPaths(configDir, _tempDir);
        var serializer = new StubXmlSerializer();
        var pluginLogger = new CaptureLogger<Plugin>();

        var plugin = new Plugin(paths, serializer, pluginLogger);
        plugin.Configuration.ApiUrl = apiUrl;
        plugin.Configuration.ApiToken = apiToken;
        if (signingSecret is not null)
        {
            plugin.Configuration.MediaSigningSecret = signingSecret;
        }
        if (defaultUserId is not null)
        {
            plugin.Configuration.DefaultUserId = defaultUserId;
        }
    }

    private static void ResetPluginInstance()
    {
        var prop = typeof(Plugin).GetProperty("Instance",
            BindingFlags.Public | BindingFlags.Static);
        prop!.SetValue(null, null);
    }

    /// <summary>
    /// Minimal IHttpClientFactory stub that returns an HttpClient wrapping the given handler.
    /// </summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>
    /// Controllable HttpMessageHandler stub. Tracks call count for cache-hit assertions.
    /// </summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;

        public int CallCount { get; private set; }

        public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn)
            => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _fn(request, cancellationToken);
        }
    }

    /// <summary>
    /// ILogger that captures formatted messages for assertion.
    /// </summary>
    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// Minimal IApplicationPaths — only PluginConfigurationsPath and PluginsPath matter for Plugin construction.
    /// </summary>
    private sealed class StubAppPaths : IApplicationPaths
    {
        public StubAppPaths(string configPath, string pluginsPath)
        {
            PluginConfigurationsPath = configPath;
            PluginsPath = pluginsPath;
        }

        public string PluginConfigurationsPath { get; }
        public string PluginsPath { get; }

        // Unused stubs
        public string ProgramDataPath => "";
        public string ProgramSystemPath => "";
        public string DataDirectory => "";
        public string DataPath => "";
        public string CachePath => "";
        public string LogDirectoryPath => "";
        public string ConfigurationDirectoryPath => "";
        public string SystemConfigurationFilePath => "";
        public string NetworkConfigurationFilePath => "";
        public string TranscodeDirectory => "";
        public string InternalMetadataPath => "";
        public string DefaultDirectory => "";
        public string VirtualDataPath => "";
        public string WebPath => "";
        public string PluginPath => "";
        public string AudioCachePath => "";
        public string MediaDefaultPath => "";
        public string EncoderPath => "";
        public string SystemCachePath => "";
        public string ImageCachePath => "";
        public string TempDirectory => "";
        public string TrickplayPath => "";
        public string BackupPath => "";
        public void MakeSanityCheckOrThrow() { }
        public void CreateAndCheckMarker(string path, string name, bool createParent = false) { }
    }

    /// <summary>
    /// Minimal IXmlSerializer — returns default instances for unknown files.
    /// </summary>
    private sealed class StubXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromBytes(Type type, byte[] bytes) => Activator.CreateInstance(type)!;
        public object DeserializeFromFile(Type type, string file) => Activator.CreateInstance(type)!;
        public object DeserializeFromStream(Type type, Stream stream) => Activator.CreateInstance(type)!;
        public void SerializeToFile(object obj, string file) { }
        public void SerializeToStream(object obj, Stream stream) { }
    }
}
