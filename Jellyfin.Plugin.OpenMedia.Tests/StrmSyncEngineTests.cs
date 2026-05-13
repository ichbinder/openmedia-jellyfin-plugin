using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class StrmSyncEngineTests : IDisposable
{
    private readonly string _tempDir;
    private const string BaseUrl = "https://api.example.com";
    private const string Token = "om_test_token";

    // 64-Zeichen-Hex Hashes (gueltiges Hash-Pattern)
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    public StrmSyncEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-strm-test-{Guid.NewGuid():N}");
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

    private static LibraryItem Item(string hash, long? tmdbId = 1234L) =>
        new(hash, tmdbId, "Test", 2024, "1000", 120, "1080p");

    [Fact]
    public void WritesStrmFilesForThreeItems()
    {
        var items = new[] { Item(HashA), Item(HashB), Item(HashC) };

        var result = StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Removed);

        foreach (var hash in new[] { HashA, HashB, HashC })
        {
            var path = Path.Combine(_tempDir, $"{hash}.strm");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Equal($"{BaseUrl}/jellyfin/stream/{hash}?token={Token}", content);
        }
    }

    [Fact]
    public void RemovesStrmForRemovedHash()
    {
        // Erst {A,B,C} schreiben
        StrmSyncEngine.Sync(_tempDir, new[] { Item(HashA), Item(HashB), Item(HashC) }, BaseUrl, Token);

        var pathA = Path.Combine(_tempDir, $"{HashA}.strm");
        var pathB = Path.Combine(_tempDir, $"{HashB}.strm");
        var pathC = Path.Combine(_tempDir, $"{HashC}.strm");
        var mtimeA = File.GetLastWriteTimeUtc(pathA);
        var mtimeC = File.GetLastWriteTimeUtc(pathC);

        // 1.1s warten damit Mtime-Aenderungen erkannt werden koennten
        System.Threading.Thread.Sleep(1100);

        // Zweiter Sync ohne B
        var result = StrmSyncEngine.Sync(_tempDir, new[] { Item(HashA), Item(HashC) }, BaseUrl, Token);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Unchanged);
        Assert.Equal(1, result.Removed);

        Assert.True(File.Exists(pathA));
        Assert.False(File.Exists(pathB));
        Assert.True(File.Exists(pathC));

        // A und C duerfen NICHT angefasst worden sein
        Assert.Equal(mtimeA, File.GetLastWriteTimeUtc(pathA));
        Assert.Equal(mtimeC, File.GetLastWriteTimeUtc(pathC));
    }

    [Fact]
    public void LeavesForeignFilesUntouched()
    {
        // Vorab eine Fremd-Datei
        var foreignPath = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(foreignPath, "wichtige Notizen");
        var foreignMtime = File.GetLastWriteTimeUtc(foreignPath);

        // .strm mit non-hash Name (sollte auch ueberleben)
        var weirdStrm = Path.Combine(_tempDir, "backup-2024.strm");
        File.WriteAllText(weirdStrm, "old data");
        var weirdMtime = File.GetLastWriteTimeUtc(weirdStrm);

        System.Threading.Thread.Sleep(1100);

        var result = StrmSyncEngine.Sync(_tempDir, new[] { Item(HashA) }, BaseUrl, Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, result.Foreign);

        Assert.True(File.Exists(foreignPath));
        Assert.Equal("wichtige Notizen", File.ReadAllText(foreignPath));
        Assert.Equal(foreignMtime, File.GetLastWriteTimeUtc(foreignPath));

        Assert.True(File.Exists(weirdStrm));
        Assert.Equal("old data", File.ReadAllText(weirdStrm));
        Assert.Equal(weirdMtime, File.GetLastWriteTimeUtc(weirdStrm));
    }

    [Fact]
    public void IsIdempotent()
    {
        var items = new[] { Item(HashA), Item(HashB) };
        StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        var pathA = Path.Combine(_tempDir, $"{HashA}.strm");
        var pathB = Path.Combine(_tempDir, $"{HashB}.strm");
        var mtimeA = File.GetLastWriteTimeUtc(pathA);
        var mtimeB = File.GetLastWriteTimeUtc(pathB);

        System.Threading.Thread.Sleep(1100);

        var result = StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Unchanged);
        Assert.Equal(0, result.Removed);

        Assert.Equal(mtimeA, File.GetLastWriteTimeUtc(pathA));
        Assert.Equal(mtimeB, File.GetLastWriteTimeUtc(pathB));
    }

    [Fact]
    public void RewritesWhenContentChanges()
    {
        // Erst mit Token A schreiben
        StrmSyncEngine.Sync(_tempDir, new[] { Item(HashA) }, BaseUrl, "old_token");

        // Jetzt mit Token B
        var result = StrmSyncEngine.Sync(_tempDir, new[] { Item(HashA) }, BaseUrl, "new_token");

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Unchanged);

        var path = Path.Combine(_tempDir, $"{HashA}.strm");
        Assert.Contains("token=new_token", File.ReadAllText(path));
    }

    [Fact]
    public void SkipsItemsWithoutTmdbId()
    {
        var items = new[]
        {
            Item(HashA, tmdbId: null),
            Item(HashB, tmdbId: 42L),
        };

        var result = StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.False(File.Exists(Path.Combine(_tempDir, $"{HashA}.strm")));
        Assert.True(File.Exists(Path.Combine(_tempDir, $"{HashB}.strm")));
    }

    [Fact]
    public void RejectsInvalidHashes()
    {
        // Hash mit Path-Escape-Versuch
        var items = new[]
        {
            new LibraryItem("../etc/passwd", 1L, "evil", 2024, null, null, null),
            new LibraryItem("not_64_hex", 1L, "evil2", 2024, null, null, null),
            new LibraryItem("UPPERCASE" + new string('a', 55), 1L, "evil3", 2024, null, null, null),
            Item(HashA),
        };

        var result = StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(3, result.Rejected);

        // Verzeichnis darf nur HashA.strm enthalten
        var entries = Directory.EnumerateFileSystemEntries(_tempDir).ToArray();
        Assert.Single(entries);
        Assert.True(File.Exists(Path.Combine(_tempDir, $"{HashA}.strm")));
    }

    [Fact]
    public void TokenIsUrlEncoded()
    {
        var url = StrmSyncEngine.BuildStreamUrl(BaseUrl, HashA, "om_test/with+special chars");

        Assert.Equal(
            $"{BaseUrl}/jellyfin/stream/{HashA}?token=om_test%2Fwith%2Bspecial%20chars",
            url);
    }

    [Fact]
    public void CreatesDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "deep", "nested", "strm");
        Assert.False(Directory.Exists(nested));

        var result = StrmSyncEngine.Sync(nested, new[] { Item(HashA) }, BaseUrl, Token);

        Assert.Equal(1, result.Added);
        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(Path.Combine(nested, $"{HashA}.strm")));
    }
}
