using System;
using System.IO;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class DiskQuotaCheckerTests : IDisposable
{
    private readonly string _tempDir;

    public DiskQuotaCheckerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-diskquota-test-{Guid.NewGuid():N}");
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
            // Best-effort cleanup
        }
    }

    [Fact]
    public void CheckTargetFolder_EnoughSpace_ReturnsOk()
    {
        // Arrange — use actual temp dir, request tiny size
        var expectedSize = 1024L; // 1 KB
        var margin = 1024L; // 1 KB

        // Act
        var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
            _tempDir, expectedSize, safetyMarginBytes: margin);

        // Assert
        Assert.True(ok, $"Should have enough space. Free={freeBytes}, Required={requiredBytes}");
        Assert.Equal(expectedSize + margin, requiredBytes);
        Assert.True(freeBytes > 0, "Free bytes should be positive on any real filesystem");
    }

    [Fact]
    public void CheckTargetFolder_TooLittleSpace_ReturnsNotOk()
    {
        // Arrange — request an absurdly large size
        var expectedSize = 1000L * 1024 * 1024 * 1024 * 1024 * 1024; // 1 EB
        var margin = 1024L;

        // Act
        var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
            _tempDir, expectedSize, safetyMarginBytes: margin);

        // Assert
        Assert.False(ok, "1 EB should never fit on any real disk");
        Assert.Equal(expectedSize + margin, requiredBytes);
    }

    [Fact]
    public void CheckTargetFolder_ZeroExpectedSize_StillRequiresMargin()
    {
        // Arrange
        var expectedSize = 0L;
        var margin = 2048L; // 2 KB

        // Act
        var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
            _tempDir, expectedSize, safetyMarginBytes: margin);

        // Assert — with zero expected size, only margin is required
        Assert.True(ok, "Only margin should be required");
        Assert.Equal(0L + margin, requiredBytes);
    }

    [Fact]
    public void CheckTargetFolder_InvalidFolder_ReturnsNotOk()
    {
        // Arrange — use a path that cannot be resolved to a root
        var invalidFolder = "";
        var expectedSize = 1024L;

        // Act
        var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
            invalidFolder, expectedSize);

        // Assert — conservative not-ok
        Assert.False(ok, "Empty folder should return not-ok");
        Assert.Equal(0, freeBytes);
    }

    [Fact]
    public void CheckTargetFolder_DefaultMargin_Is20GB()
    {
        // Verify the constant
        var expected20GB = 20L * 1024 * 1024 * 1024;
        Assert.Equal(expected20GB, DiskQuotaChecker.DefaultSafetyMarginBytes);
    }

    [Fact]
    public void CheckTargetFolder_NonExistentRoot_ThrowsCaught()
    {
        // On some platforms, a non-existent drive root may throw
        // This test validates the try/catch returns conservative not-ok
        // Use a path on a drive letter that likely doesn't exist
        var nonExistent = "/nonexistent-test-drive-volume/path";

        var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
            nonExistent, 1024L);

        // On most systems, non-existent drive returns not-ok
        // (either throws caught → false, or reports 0 free)
        // On Linux this will likely succeed since / resolves, so we just verify it doesn't throw
        Assert.True(freeBytes >= 0, "Should not throw");
    }

    [Fact]
    public void CheckTargetFolder_FakeFreeBytes_ReturnsFakeValue()
    {
        // Arrange — set OM_FAKE_FREE_BYTES to a small value
        var fakeValue = 1000000L; // ~1 MB
        Environment.SetEnvironmentVariable("OM_FAKE_FREE_BYTES", fakeValue.ToString());
        try
        {
            // Act — request size that would normally pass (1 KB)
            // but with fake free bytes = 1 MB, the 20 GB default margin makes it fail
            var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
                _tempDir, 1024L);

            // Assert
            Assert.Equal(fakeValue, freeBytes);
            Assert.False(ok, "With only 1 MB fake free and 20 GB margin, should be not-ok");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OM_FAKE_FREE_BYTES", null);
        }
    }

    [Fact]
    public void CheckTargetFolder_FakeFreeBytes_InvalidValue_FallsThrough()
    {
        // Arrange — set invalid OM_FAKE_FREE_BYTES
        Environment.SetEnvironmentVariable("OM_FAKE_FREE_BYTES", "not-a-number");
        try
        {
            // Act — should fall through to real DriveInfo
            var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
                _tempDir, 1024L, safetyMarginBytes: 1024L);

            // Assert — uses real free bytes (should pass for 1 KB request)
            Assert.True(ok, "Should use real free bytes when fake value is invalid");
            Assert.True(freeBytes > 0, "Real free bytes should be positive");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OM_FAKE_FREE_BYTES", null);
        }
    }

    [Fact]
    public void CheckTargetFolder_FakeFreeBytes_LargeValue_ReturnsOk()
    {
        // Arrange — set a huge fake free bytes
        var fakeValue = 1000L * 1024 * 1024 * 1024 * 1024; // 1 PB
        Environment.SetEnvironmentVariable("OM_FAKE_FREE_BYTES", fakeValue.ToString());
        try
        {
            // Act — even with 20 GB margin, 1 PB should be enough
            var (ok, freeBytes, requiredBytes) = DiskQuotaChecker.CheckTargetFolder(
                _tempDir, 500L * 1024 * 1024 * 1024); // 500 GB file

            // Assert
            Assert.Equal(fakeValue, freeBytes);
            Assert.True(ok, "1 PB fake free should always pass");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OM_FAKE_FREE_BYTES", null);
        }
    }
}
