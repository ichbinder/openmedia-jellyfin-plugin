using System;
using System.IO;
using Jellyfin.Plugin.OpenMedia.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class BootstrapReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestApplicationPaths _paths;

    public BootstrapReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-bootstrap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _paths = new TestApplicationPaths(_tempDir);
    }

    public void Dispose()
    {
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

    private string ConfigDir => _paths.PluginConfigurationsPath;

    private string BootstrapPath => Path.Combine(ConfigDir, BootstrapReader.FileName);

    private PluginConfiguration FreshConfig() => new()
    {
        ApiUrl = string.Empty,
        ApiToken = string.Empty,
        MediaSigningSecret = string.Empty
    };

    private PluginConfiguration FilledConfig() => new()
    {
        ApiUrl = "https://existing.example.com",
        ApiToken = "om_already_set",
        MediaSigningSecret = "existing_secret_that_is_exactly_32_charss"
    };

    [Fact]
    public void TryApply_NoFile_ReturnsFalse()
    {
        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);
        Assert.False(result);
    }

    [Fact]
    public void TryApply_EmptyConfig_WithBootstrap_SetsConfig_DeletesFile()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.mediatoken.de", "apiToken": "om_abc123" }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.True(result);
        Assert.Equal("https://api.mediatoken.de", config.ApiUrl);
        Assert.Equal("om_abc123", config.ApiToken);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_FilledConfig_WithBootstrap_SkipsConfig_DeletesFile()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.mediatoken.de", "apiToken": "om_abc123" }""");

        var config = FilledConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        // Should NOT apply because config already has values
        Assert.False(result);
        Assert.Equal("https://existing.example.com", config.ApiUrl);
        Assert.Equal("om_already_set", config.ApiToken);
        // But file MUST be deleted
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_PartiallyFilledConfig_OnlyFillsEmptyFields()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.mediatoken.de", "apiToken": "om_newtoken" }""");

        var config = new PluginConfiguration
        {
            ApiUrl = string.Empty,              // empty — should be filled
            ApiToken = "om_already_set"          // already set — should be kept
        };

        var result = BootstrapReader.TryApply(_paths, config);

        Assert.True(result); // partially applied
        Assert.Equal("https://api.mediatoken.de", config.ApiUrl); // filled from bootstrap
        Assert.Equal("om_already_set", config.ApiToken);            // kept existing
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_InvalidJson_DeletesFile_NoCrash()
    {
        File.WriteAllText(BootstrapPath, "not valid json {{{");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.False(result);
        Assert.Empty(config.ApiUrl);    // still empty (not crashed)
        Assert.Empty(config.ApiToken);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_MissingApiUrl_DeletesFile()
    {
        File.WriteAllText(BootstrapPath, """{ "apiToken": "om_x" }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.False(result);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_MissingApiToken_DeletesFile()
    {
        File.WriteAllText(BootstrapPath, """{ "apiUrl": "https://x" }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.False(result);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_EmptyTokenString_DeletesFile()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://x", "apiToken": "   " }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.False(result);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_CaseInsensitiveAndExtraFields()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "ApiUrl": "https://x", "APITOKEN": "om_y", "extra": 42 }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.True(result);
        Assert.Equal("https://x", config.ApiUrl);
        Assert.Equal("om_y", config.ApiToken);
    }

    [Fact]
    public void TryApply_DllDirectoryFallback()
    {
        // No file in PluginConfigurationsPath
        var dllDir = Path.Combine(_tempDir, "dll-dir");
        Directory.CreateDirectory(dllDir);
        File.WriteAllText(
            Path.Combine(dllDir, BootstrapReader.FileName),
            """{ "apiUrl": "https://dll-fallback", "apiToken": "om_dll" }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config, dllDir);

        Assert.True(result);
        Assert.Equal("https://dll-fallback", config.ApiUrl);
        Assert.Equal("om_dll", config.ApiToken);
    }

    [Fact]
    public void TryApply_ConfigurationsPathTakesPrecedence()
    {
        // File in BOTH directories — PluginConfigurationsPath wins
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://config-path", "apiToken": "om_config" }""");

        var dllDir = Path.Combine(_tempDir, "dll-dir");
        Directory.CreateDirectory(dllDir);
        File.WriteAllText(
            Path.Combine(dllDir, BootstrapReader.FileName),
            """{ "apiUrl": "https://dll-path", "apiToken": "om_dll" }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config, dllDir);

        Assert.True(result);
        Assert.Equal("https://config-path", config.ApiUrl);
        Assert.Equal("om_config", config.ApiToken);
    }

    [Fact]
    public void TryApply_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BootstrapReader.TryApply(_paths, null!));
    }

    [Fact]
    public void TryApply_MediaSigningSecret_SetsOnEmptyConfig()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.mediatoken.de", "apiToken": "om_abc123", "media_signing_secret": "abcdefghijklmnopqrstuvwxyz123456" }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.True(result);
        Assert.Equal("https://api.mediatoken.de", config.ApiUrl);
        Assert.Equal("om_abc123", config.ApiToken);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz123456", config.MediaSigningSecret);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_MediaSigningSecret_Idempotent_DoesNotOverwrite()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.mediatoken.de", "apiToken": "om_abc123", "media_signing_secret": "new_secret_value_that_is_32_chars_xx" }""");

        var config = new PluginConfiguration
        {
            ApiUrl = string.Empty,
            ApiToken = string.Empty,
            MediaSigningSecret = "existing_secret_value_that_is_32_chars!!"
        };

        var result = BootstrapReader.TryApply(_paths, config);

        Assert.True(result);
        Assert.Equal("https://api.mediatoken.de", config.ApiUrl);
        Assert.Equal("om_abc123", config.ApiToken);
        // Existing secret must NOT be overwritten
        Assert.Equal("existing_secret_value_that_is_32_chars!!", config.MediaSigningSecret);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_MediaSigningSecret_Missing_DoesNotError()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.mediatoken.de", "apiToken": "om_abc123" }""");

        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config);

        Assert.True(result);
        Assert.Equal(string.Empty, config.MediaSigningSecret);
        Assert.False(File.Exists(BootstrapPath));
    }

    [Fact]
    public void TryApply_WithLogger_DoesNotThrow()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.mediatoken.de", "apiToken": "om_abc123" }""");

        var logger = new TestLogger<BootstrapReaderTests>();
        var config = FreshConfig();
        var result = BootstrapReader.TryApply(_paths, config, logger: logger);

        Assert.True(result);
    }

    /// <summary>
    /// Minimal IApplicationPaths test double — only PluginConfigurationsPath needed.
    /// </summary>
    private sealed class TestApplicationPaths : IApplicationPaths
    {
        public TestApplicationPaths(string tempDir)
        {
            PluginConfigurationsPath = Path.Combine(tempDir, "config");
            Directory.CreateDirectory(PluginConfigurationsPath);
        }

        public string PluginConfigurationsPath { get; }

        // Unused — stubbed
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
        public string PluginsPath => "";
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
    /// Simple ILogger test double that captures nothing — just verifies no exceptions from logging.
    /// </summary>
    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
