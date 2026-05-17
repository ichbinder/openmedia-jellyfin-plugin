using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class PrecacheTtlCleanupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PrecacheStateStore _stateStore;
    private readonly PrecacheTtlCleanupService _service;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public PrecacheTtlCleanupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-ttl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _stateStore = new PrecacheStateStore(_tempDir, NullLogger<PrecacheStateStore>.Instance);
        _service = new PrecacheTtlCleanupService(
            NullLogger<PrecacheTtlCleanupService>.Instance,
            _stateStore);
    }

    public void Dispose()
    {
        _stateStore.Dispose();
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

    /// <summary>
    /// Seeds an entry with a specific lastEventAt by writing directly to the state file.
    /// This bypasses UpdateAsync which always sets lastEventAt = DateTime.UtcNow.
    /// </summary>
    private async Task SeedEntryDirectAsync(
        string hash,
        PrecacheState state,
        int? ttlSeconds = null,
        DateTime? lastEventAt = null,
        string? localPath = null)
    {
        // Use the store to create the entry, then patch the timestamp directly
        await _stateStore.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = state,
            DownloadedBytes = state == PrecacheState.Done ? 1024 : 0,
            SizeBytes = 1024,
            TtlSeconds = ttlSeconds,
            LocalPath = localPath,
        }, CancellationToken.None);

        // Now patch the lastEventAt directly in the JSON file
        if (lastEventAt.HasValue)
        {
            await PatchLastEventAtAsync(hash, lastEventAt.Value);
        }
    }

    private async Task PatchLastEventAtAsync(string hash, DateTime lastEventAt)
    {
        // Access the state file directly
        var stateFile = Path.Combine(_tempDir, "openmedia", "precache-state.json");
        var json = await File.ReadAllTextAsync(stateFile);
        using var doc = JsonDocument.Parse(json);

        // Reconstruct with patched value
        var entries = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.GetProperty("entries").EnumerateObject())
        {
            var entryDict = new Dictionary<string, object?>();
            foreach (var field in prop.Value.EnumerateObject())
            {
                if (field.Name == "lastEventAt" && prop.Name == hash)
                {
                    entryDict["lastEventAt"] = lastEventAt.ToString("O");
                }
                else
                {
                    entryDict[field.Name] = field.Value.ValueKind switch
                    {
                        JsonValueKind.String => field.Value.GetString(),
                        JsonValueKind.Number => field.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => field.Value.GetRawText()
                    };
                }
            }
            entries[prop.Name] = entryDict;
        }

        var rootDict = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["entries"] = entries
        };

        var newJson = JsonSerializer.Serialize(rootDict, JsonOptions);
        await File.WriteAllTextAsync(stateFile, newJson);
    }

    [Fact]
    public async Task TwoExpiredOneFresh_TwoEvicted()
    {
        // Arrange: 3 done items, 2 expired, 1 fresh
        var expiredTime = DateTime.UtcNow.AddDays(-10);
        var freshTime = DateTime.UtcNow.AddHours(-1);

        // Create fake files for expired entries
        var file1 = Path.Combine(_tempDir, "expired1.mp4");
        var file2 = Path.Combine(_tempDir, "expired2.mp4");
        await File.WriteAllTextAsync(file1, "fake");
        await File.WriteAllTextAsync(file2, "fake");

        await SeedEntryDirectAsync("hash1", PrecacheState.Done, ttlSeconds: 60, lastEventAt: expiredTime, localPath: file1);
        await SeedEntryDirectAsync("hash2", PrecacheState.Done, ttlSeconds: 60, lastEventAt: expiredTime, localPath: file2);
        await SeedEntryDirectAsync("hash3", PrecacheState.Done, ttlSeconds: 604800, lastEventAt: freshTime);

        var progress = new Progress<double>();

        // Act
        await _service.ExecuteAsync(progress, CancellationToken.None);

        // Assert: hash1 and hash2 evicted, hash3 remains
        var remaining = await _stateStore.GetAllAsync(CancellationToken.None);
        Assert.Single(remaining);
        Assert.True(remaining.ContainsKey("hash3"));

        // Files deleted
        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
    }

    [Fact]
    public async Task FileDeleteError_ContinuesWithNextItem()
    {
        // Arrange: 2 expired items, one with a non-existent path
        var expiredTime = DateTime.UtcNow.AddDays(-10);

        var file1 = Path.Combine(_tempDir, "existing.mp4");
        await File.WriteAllTextAsync(file1, "fake");

        await SeedEntryDirectAsync("hash1", PrecacheState.Done, ttlSeconds: 60, lastEventAt: expiredTime, localPath: file1);
        await SeedEntryDirectAsync("hash2", PrecacheState.Done, ttlSeconds: 60, lastEventAt: expiredTime, localPath: "/nonexistent/path/file.mp4");

        var progress = new Progress<double>();

        // Act — should not throw
        await _service.ExecuteAsync(progress, CancellationToken.None);

        // Assert: both entries removed (hash1 file deleted, hash2 file didn't exist so delete is no-op)
        var remaining = await _stateStore.GetAllAsync(CancellationToken.None);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task EmptyState_NoOp()
    {
        // Arrange: no entries
        var progress = new Progress<double>();

        // Act — should not throw
        await _service.ExecuteAsync(progress, CancellationToken.None);

        // Assert: still empty
        var all = await _stateStore.GetAllAsync(CancellationToken.None);
        Assert.Empty(all);
    }

    [Fact]
    public async Task OnlyNonDoneEntries_NothingEvicted()
    {
        // Arrange: entries in non-done states
        await SeedEntryDirectAsync("hash1", PrecacheState.Pending);
        await SeedEntryDirectAsync("hash2", PrecacheState.Downloading);
        await SeedEntryDirectAsync("hash3", PrecacheState.Failed);

        var progress = new Progress<double>();

        // Act
        await _service.ExecuteAsync(progress, CancellationToken.None);

        // Assert: all entries remain
        var remaining = await _stateStore.GetAllAsync(CancellationToken.None);
        Assert.Equal(3, remaining.Count);
    }

    [Fact]
    public void DefaultTtl_Is7Days()
    {
        Assert.Equal(7 * 24 * 60 * 60, PrecacheTtlCleanupService.DefaultTtlSeconds);
    }

    [Fact]
    public async Task EntryWithNullTtl_UsesDefault7Days()
    {
        // Arrange: entry expired 8 days ago with null TTL (uses default 7 days)
        var expiredTime = DateTime.UtcNow.AddDays(-8);
        var file1 = Path.Combine(_tempDir, "default-ttl.mp4");
        await File.WriteAllTextAsync(file1, "fake");

        await SeedEntryDirectAsync("hash1", PrecacheState.Done, ttlSeconds: null, lastEventAt: expiredTime, localPath: file1);

        var progress = new Progress<double>();

        // Act
        await _service.ExecuteAsync(progress, CancellationToken.None);

        // Assert: evicted (8 days > 7 days default)
        var remaining = await _stateStore.GetAllAsync(CancellationToken.None);
        Assert.Empty(remaining);
        Assert.False(File.Exists(file1));
    }
}
