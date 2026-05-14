using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.OpenMedia.Bootstrap;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class BootstrapLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public BootstrapLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-bootstrap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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

    private string BootstrapPath => Path.Combine(_tempDir, BootstrapLoader.FileName);

    private string InvalidPath => BootstrapPath + ".invalid";

    [Fact]
    public void TryApply_NoFile_ReturnsFalse_DoesNotInvokeCallback()
    {
        var invoked = false;
        var result = BootstrapLoader.TryApply(_tempDir, (_, _) => invoked = true);

        Assert.False(result);
        Assert.False(invoked);
    }

    [Fact]
    public void TryApply_ValidFile_InvokesCallback_DeletesFile()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://api.example.com", "apiToken": "om_abc123" }""");

        var captured = new List<(string url, string token)>();
        var result = BootstrapLoader.TryApply(
            _tempDir,
            (url, token) => captured.Add((url, token)));

        Assert.True(result);
        Assert.Single(captured);
        Assert.Equal("https://api.example.com", captured[0].url);
        Assert.Equal("om_abc123", captured[0].token);
        Assert.False(File.Exists(BootstrapPath));
        Assert.False(File.Exists(InvalidPath));
    }

    [Fact]
    public void TryApply_ValidFile_AcceptsCaseInsensitiveAndExtraFields()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "ApiUrl": "https://x", "APITOKEN": "om_y", "extraField": 42 }""");

        string? capturedUrl = null;
        string? capturedToken = null;
        var result = BootstrapLoader.TryApply(
            _tempDir,
            (url, token) =>
            {
                capturedUrl = url;
                capturedToken = token;
            });

        Assert.True(result);
        Assert.Equal("https://x", capturedUrl);
        Assert.Equal("om_y", capturedToken);
    }

    [Fact]
    public void TryApply_InvalidJson_RenamesToInvalid_ReturnsFalse()
    {
        File.WriteAllText(BootstrapPath, "not json {{{");

        var invoked = false;
        var result = BootstrapLoader.TryApply(_tempDir, (_, _) => invoked = true);

        Assert.False(result);
        Assert.False(invoked);
        Assert.False(File.Exists(BootstrapPath));
        Assert.True(File.Exists(InvalidPath));
    }

    [Fact]
    public void TryApply_MissingApiUrl_RenamesToInvalid()
    {
        File.WriteAllText(BootstrapPath, """{ "apiToken": "om_x" }""");

        var result = BootstrapLoader.TryApply(_tempDir, (_, _) => { });

        Assert.False(result);
        Assert.False(File.Exists(BootstrapPath));
        Assert.True(File.Exists(InvalidPath));
    }

    [Fact]
    public void TryApply_MissingApiToken_RenamesToInvalid()
    {
        File.WriteAllText(BootstrapPath, """{ "apiUrl": "https://x" }""");

        var result = BootstrapLoader.TryApply(_tempDir, (_, _) => { });

        Assert.False(result);
        Assert.False(File.Exists(BootstrapPath));
        Assert.True(File.Exists(InvalidPath));
    }

    [Fact]
    public void TryApply_EmptyTokenString_RenamesToInvalid()
    {
        File.WriteAllText(
            BootstrapPath,
            """{ "apiUrl": "https://x", "apiToken": "   " }""");

        var result = BootstrapLoader.TryApply(_tempDir, (_, _) => { });

        Assert.False(result);
        Assert.True(File.Exists(InvalidPath));
    }

    [Fact]
    public void TryApply_PreExistingInvalidFile_GetsOverwritten()
    {
        File.WriteAllText(InvalidPath, "old garbage");
        File.WriteAllText(BootstrapPath, "new garbage");

        var result = BootstrapLoader.TryApply(_tempDir, (_, _) => { });

        Assert.False(result);
        Assert.False(File.Exists(BootstrapPath));
        Assert.True(File.Exists(InvalidPath));
        Assert.Equal("new garbage", File.ReadAllText(InvalidPath));
    }

    [Fact]
    public void TryApply_EmptyDirectory_ReturnsFalse()
    {
        var result = BootstrapLoader.TryApply(string.Empty, (_, _) => { });
        Assert.False(result);
    }

    [Fact]
    public void TryApply_NullCallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BootstrapLoader.TryApply(_tempDir, null!));
    }
}
