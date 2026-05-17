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

public sealed class PrecacheStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PrecacheStateStore _store;

    public PrecacheStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"precache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new PrecacheStateStore(_tempDir, NullLogger<PrecacheStateStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private string StatePath => Path.Combine(_tempDir, "openmedia", "precache-state.json");

    [Fact]
    public async Task GetAllAsync_Empty_When_No_File()
    {
        var all = await _store.GetAllAsync(CancellationToken.None);
        Assert.Empty(all);
    }

    [Fact]
    public async Task UpdateAsync_Create_And_Get()
    {
        const string hash = "abc123";
        await _store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Downloading,
            DownloadedBytes = 1024,
            SizeBytes = 5000,
        }, CancellationToken.None);

        var entry = await _store.GetAsync(hash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Downloading, entry.State);
        Assert.Equal(1024, entry.DownloadedBytes);
        Assert.Equal(5000, entry.SizeBytes);
        Assert.True(entry.LastEventAt > DateTime.MinValue);
    }

    [Fact]
    public async Task Persisted_Across_New_Instance()
    {
        const string hash = "def456";
        await _store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 9999,
            SizeBytes = 9999,
            Sha256 = "sha256hash",
        }, CancellationToken.None);

        // Neue Instanz auf gleichem Pfad
        using var store2 = new PrecacheStateStore(_tempDir, NullLogger<PrecacheStateStore>.Instance);
        var entry = await store2.GetAsync(hash, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Done, entry.State);
        Assert.Equal(9999, entry.DownloadedBytes);
        Assert.Equal("sha256hash", entry.Sha256);
    }

    [Fact]
    public async Task Concurrent_UpdateAsync_Same_Hash()
    {
        const string hash = "concurrent123";
        const int iterations = 20;

        var tasks = Enumerable.Range(0, iterations).Select(i =>
            _store.UpdateAsync(hash, prev => new PrecacheEntry
            {
                State = PrecacheState.Downloading,
                DownloadedBytes = (prev?.DownloadedBytes ?? 0) + 100,
                SizeBytes = 5000,
            }, CancellationToken.None));

        await Task.WhenAll(tasks);

        var entry = await _store.GetAsync(hash, CancellationToken.None);
        Assert.NotNull(entry);
        // All increments must be applied (SemaphoreSlim serialization)
        Assert.Equal(iterations * 100, entry.DownloadedBytes);
    }

    [Fact]
    public async Task Corrupted_JSON_Creates_Backup_And_Empty_State()
    {
        // Write garbage to the state file
        var dir = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(StatePath, "{{{{invalid json!!!!");

        var all = await _store.GetAllAsync(CancellationToken.None);
        Assert.Empty(all);

        // Backup file should exist
        var backupFiles = Directory.GetFiles(dir, "precache-state.json.corrupt-*");
        Assert.Single(backupFiles);
    }

    [Fact]
    public async Task IsCached_False_When_State_Not_Done()
    {
        const string hash = "notdone";
        await _store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Downloading,
            DownloadedBytes = 500,
            SizeBytes = 1000,
        }, CancellationToken.None);

        var result = await _store.IsCachedAsync(hash, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task IsCached_False_When_Done_But_File_Missing()
    {
        const string hash = "donemissing";
        await _store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 1000,
            SizeBytes = 1000,
            LocalPath = "/nonexistent/path/abc.mp4",
        }, CancellationToken.None);

        var result = await _store.IsCachedAsync(hash, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task IsCached_True_When_Done_And_File_Exists()
    {
        const string hash = "cached123";

        // Create a real file
        var filePath = Path.Combine(_tempDir, "cached123.mp4");
        await File.WriteAllTextAsync(filePath, "fake video");

        await _store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 10,
            SizeBytes = 10,
            LocalPath = filePath,
        }, CancellationToken.None);

        var result = await _store.IsCachedAsync(hash, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task RemoveAsync_Deletes_Entry()
    {
        const string hash = "toremove";
        await _store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Pending,
            DownloadedBytes = 0,
            SizeBytes = 100,
        }, CancellationToken.None);

        await _store.RemoveAsync(hash, CancellationToken.None);

        var entry = await _store.GetAsync(hash, CancellationToken.None);
        Assert.Null(entry);
    }

    [Fact]
    public async Task StateFile_Has_Correct_Schema()
    {
        await _store.UpdateAsync("hash1", _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 100,
            SizeBytes = 100,
        }, CancellationToken.None);

        var json = await File.ReadAllTextAsync(StatePath);
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out var sv));
        Assert.Equal(1, sv.GetInt32());

        Assert.True(doc.RootElement.TryGetProperty("entries", out var entries));
        Assert.True(entries.TryGetProperty("hash1", out _));
    }

    [Fact]
    public async Task UpdateAsync_Mutator_Returns_Null_Removes_Entry()
    {
        const string hash = "nullmutator";
        await _store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Pending,
            DownloadedBytes = 0,
            SizeBytes = 100,
        }, CancellationToken.None);

        // Mutator returns null → remove
        await _store.UpdateAsync(hash, _ => null, CancellationToken.None);

        var entry = await _store.GetAsync(hash, CancellationToken.None);
        Assert.Null(entry);
    }
}
