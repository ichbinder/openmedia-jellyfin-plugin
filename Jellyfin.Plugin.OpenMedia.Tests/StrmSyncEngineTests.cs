using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static LibraryItem Item(
        string hash,
        long? tmdbId = 1234L,
        string? title = "Test Movie",
        int? year = 2024) =>
        new(hash, tmdbId, title, year, "1000", 120, "1080p");

    private static string FolderFor(string title, int? year, long tmdbId) =>
        StrmSyncEngine.BuildFolderName(title, year, tmdbId);

    [Fact]
    public void WritesStrmFilesForThreeItems()
    {
        var items = new[]
        {
            Item(HashA, tmdbId: 1001L, title: "Alpha", year: 2020),
            Item(HashB, tmdbId: 1002L, title: "Beta",  year: 2021),
            Item(HashC, tmdbId: 1003L, title: "Gamma", year: 2022),
        };

        var result = StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.Removed);

        foreach (var item in items)
        {
            var folder = FolderFor(item.Title!, item.Year, item.TmdbId!.Value);
            var path = Path.Combine(_tempDir, folder, $"{item.Hash}.strm");
            Assert.True(File.Exists(path), $"Erwartet {path}");
            var content = File.ReadAllText(path);
            Assert.Equal($"{BaseUrl}/jellyfin/stream/{item.Hash}?token={Token}", content);
        }
    }

    [Fact]
    public void FolderNameContainsTmdbIdTag()
    {
        var items = new[] { Item(HashA, tmdbId: 27205L, title: "Inception", year: 2010) };

        StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        var expected = Path.Combine(_tempDir, "Inception (2010) [tmdbid-27205]", $"{HashA}.strm");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void OmitsYearWhenNull()
    {
        var items = new[] { Item(HashA, tmdbId: 42L, title: "Unknown Year", year: null) };

        StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        var expected = Path.Combine(_tempDir, "Unknown Year [tmdbid-42]", $"{HashA}.strm");
        Assert.True(File.Exists(expected));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "Unknown Year () [tmdbid-42]")));
    }

    [Fact]
    public void SanitizesForbiddenCharsInTitle()
    {
        // Sonderzeichen werden zu Space, mehrfache Spaces kollabieren.
        var items = new[]
        {
            Item(HashA, tmdbId: 7L, title: "Spider-Man: Across the Spider-Verse", year: 2023),
            Item(HashB, tmdbId: 8L, title: "Wall/E?\"<>|", year: 2008),
            Item(HashC, tmdbId: 9L, title: "  Lots   of    spaces  ", year: 2024),
        };

        StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.True(File.Exists(Path.Combine(
            _tempDir,
            "Spider-Man Across the Spider-Verse (2023) [tmdbid-7]",
            $"{HashA}.strm")));
        Assert.True(File.Exists(Path.Combine(
            _tempDir,
            "Wall E (2008) [tmdbid-8]",
            $"{HashB}.strm")));
        Assert.True(File.Exists(Path.Combine(
            _tempDir,
            "Lots of spaces (2024) [tmdbid-9]",
            $"{HashC}.strm")));
    }

    [Fact]
    public void FallsBackToUntitledWhenTitleEmpty()
    {
        var items = new[] { Item(HashA, tmdbId: 99L, title: "", year: 2024) };

        StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.True(File.Exists(Path.Combine(
            _tempDir,
            "Untitled (2024) [tmdbid-99]",
            $"{HashA}.strm")));
    }

    [Fact]
    public void RemovesStrmAndEmptyFolderForRemovedHash()
    {
        // Erst alle drei legen
        StrmSyncEngine.Sync(
            _tempDir,
            new[]
            {
                Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020),
                Item(HashB, tmdbId: 2L, title: "Beta",  year: 2021),
                Item(HashC, tmdbId: 3L, title: "Gamma", year: 2022),
            },
            BaseUrl,
            Token);

        var folderA = Path.Combine(_tempDir, FolderFor("Alpha", 2020, 1L));
        var folderB = Path.Combine(_tempDir, FolderFor("Beta",  2021, 2L));
        var folderC = Path.Combine(_tempDir, FolderFor("Gamma", 2022, 3L));
        var pathA = Path.Combine(folderA, $"{HashA}.strm");
        var pathB = Path.Combine(folderB, $"{HashB}.strm");
        var pathC = Path.Combine(folderC, $"{HashC}.strm");

        var mtimeA = File.GetLastWriteTimeUtc(pathA);
        var mtimeC = File.GetLastWriteTimeUtc(pathC);

        // 1.1s warten damit Mtime-Aenderungen erkannt werden koennten
        System.Threading.Thread.Sleep(1100);

        // Zweiter Sync ohne B
        var result = StrmSyncEngine.Sync(
            _tempDir,
            new[]
            {
                Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020),
                Item(HashC, tmdbId: 3L, title: "Gamma", year: 2022),
            },
            BaseUrl,
            Token);

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Unchanged);
        Assert.Equal(1, result.Removed);

        Assert.True(File.Exists(pathA));
        Assert.False(File.Exists(pathB));
        Assert.False(Directory.Exists(folderB), "Leerer Ordner muss entfernt werden.");
        Assert.True(File.Exists(pathC));

        // A und C duerfen NICHT angefasst worden sein.
        Assert.Equal(mtimeA, File.GetLastWriteTimeUtc(pathA));
        Assert.Equal(mtimeC, File.GetLastWriteTimeUtc(pathC));
    }

    [Fact]
    public void RenameMovesFileToNewFolderAndRemovesOld()
    {
        // Erst mit altem Titel
        StrmSyncEngine.Sync(
            _tempDir,
            new[] { Item(HashA, tmdbId: 1L, title: "Old Name", year: 2020) },
            BaseUrl,
            Token);

        var oldFolder = Path.Combine(_tempDir, FolderFor("Old Name", 2020, 1L));
        var oldPath = Path.Combine(oldFolder, $"{HashA}.strm");
        Assert.True(File.Exists(oldPath));

        // Jetzt mit korrigiertem Titel — Hash bleibt
        var result = StrmSyncEngine.Sync(
            _tempDir,
            new[] { Item(HashA, tmdbId: 1L, title: "Correct Name", year: 2020) },
            BaseUrl,
            Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Unchanged);

        Assert.False(Directory.Exists(oldFolder), "Alter (jetzt leerer) Ordner muss weg.");
        var newPath = Path.Combine(_tempDir, FolderFor("Correct Name", 2020, 1L), $"{HashA}.strm");
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public void MigratesLegacyFlatStrmAtRoot()
    {
        // Simuliere vorigen Plugin-Stand: flat {hash}.strm im Root.
        var legacyPath = Path.Combine(_tempDir, $"{HashA}.strm");
        File.WriteAllText(legacyPath, $"{BaseUrl}/jellyfin/stream/{HashA}?token=old");

        var result = StrmSyncEngine.Sync(
            _tempDir,
            new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) },
            BaseUrl,
            Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.False(File.Exists(legacyPath));

        var newPath = Path.Combine(_tempDir, FolderFor("Alpha", 2020, 1L), $"{HashA}.strm");
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public void LeavesForeignFilesAndFoldersUntouched()
    {
        // Vorab eine Fremd-Datei
        var foreignFile = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(foreignFile, "wichtige Notizen");
        var foreignMtime = File.GetLastWriteTimeUtc(foreignFile);

        // .strm mit non-hash Name (sollte auch ueberleben)
        var weirdStrm = Path.Combine(_tempDir, "backup-2024.strm");
        File.WriteAllText(weirdStrm, "old data");
        var weirdMtime = File.GetLastWriteTimeUtc(weirdStrm);

        // Fremder Ordner ohne tmdbid-Suffix
        var foreignDir = Path.Combine(_tempDir, "My other movies");
        Directory.CreateDirectory(foreignDir);
        var foreignDirFile = Path.Combine(foreignDir, "anything.mp4");
        File.WriteAllText(foreignDirFile, "data");
        var foreignDirMtime = File.GetLastWriteTimeUtc(foreignDirFile);

        System.Threading.Thread.Sleep(1100);

        var result = StrmSyncEngine.Sync(
            _tempDir,
            new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) },
            BaseUrl,
            Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Removed);
        // 2x flat .strm-Foreign (notes.txt, backup-2024.strm) + 1 Foreign-Folder
        Assert.Equal(3, result.Foreign);

        Assert.True(File.Exists(foreignFile));
        Assert.Equal("wichtige Notizen", File.ReadAllText(foreignFile));
        Assert.Equal(foreignMtime, File.GetLastWriteTimeUtc(foreignFile));

        Assert.True(File.Exists(weirdStrm));
        Assert.Equal("old data", File.ReadAllText(weirdStrm));
        Assert.Equal(weirdMtime, File.GetLastWriteTimeUtc(weirdStrm));

        Assert.True(Directory.Exists(foreignDir));
        Assert.True(File.Exists(foreignDirFile));
        Assert.Equal(foreignDirMtime, File.GetLastWriteTimeUtc(foreignDirFile));
    }

    [Fact]
    public void IsIdempotent()
    {
        var items = new[]
        {
            Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020),
            Item(HashB, tmdbId: 2L, title: "Beta",  year: 2021),
        };
        StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        var pathA = Path.Combine(_tempDir, FolderFor("Alpha", 2020, 1L), $"{HashA}.strm");
        var pathB = Path.Combine(_tempDir, FolderFor("Beta",  2021, 2L), $"{HashB}.strm");
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
    public void RewritesOnlyWhenContentChanges()
    {
        // Erst mit Token A schreiben
        StrmSyncEngine.Sync(
            _tempDir,
            new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) },
            BaseUrl,
            "old_token");

        // Jetzt mit Token B
        var result = StrmSyncEngine.Sync(
            _tempDir,
            new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) },
            BaseUrl,
            "new_token");

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Unchanged);

        var path = Path.Combine(_tempDir, FolderFor("Alpha", 2020, 1L), $"{HashA}.strm");
        Assert.Contains("token=new_token", File.ReadAllText(path));
    }

    [Fact]
    public void SkipsItemsWithoutTmdbId()
    {
        var items = new[]
        {
            Item(HashA, tmdbId: null),
            Item(HashB, tmdbId: 42L, title: "Has TMDB", year: 2024),
        };

        var result = StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);

        // Genau ein erzeugter Ordner: der fuer HashB / TMDB 42
        var dirs = Directory.EnumerateDirectories(_tempDir).Select(Path.GetFileName).ToArray();
        Assert.Single(dirs);
        Assert.Equal(FolderFor("Has TMDB", 2024, 42L), dirs[0]);
        Assert.True(File.Exists(Path.Combine(
            _tempDir, FolderFor("Has TMDB", 2024, 42L), $"{HashB}.strm")));
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
            Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020),
        };

        var result = StrmSyncEngine.Sync(_tempDir, items, BaseUrl, Token);

        Assert.Equal(1, result.Added);
        Assert.Equal(3, result.Rejected);

        // Nur der erlaubte Ordner darf da sein.
        var dirs = Directory.EnumerateDirectories(_tempDir).Select(Path.GetFileName).ToArray();
        Assert.Single(dirs);
        Assert.Equal(FolderFor("Alpha", 2020, 1L), dirs[0]);
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

        var result = StrmSyncEngine.Sync(
            nested,
            new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) },
            BaseUrl,
            Token);

        Assert.Equal(1, result.Added);
        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(Path.Combine(
            nested, FolderFor("Alpha", 2020, 1L), $"{HashA}.strm")));
    }

    [Fact]
    public void SanitizeTitleHelperBehavior()
    {
        Assert.Equal(string.Empty, StrmSyncEngine.SanitizeTitle(null));
        Assert.Equal(string.Empty, StrmSyncEngine.SanitizeTitle("   "));
        Assert.Equal("Hello World", StrmSyncEngine.SanitizeTitle("Hello   World"));
        Assert.Equal("Hello-World!", StrmSyncEngine.SanitizeTitle("Hello-World!"));
        Assert.Equal("It's a (great) movie", StrmSyncEngine.SanitizeTitle("It's a (great) movie"));
        Assert.Equal("a b", StrmSyncEngine.SanitizeTitle("a/b"));
        Assert.Equal("a b", StrmSyncEngine.SanitizeTitle("a\\b"));
        Assert.Equal("a b", StrmSyncEngine.SanitizeTitle("a:b"));
        // Sonderzeichen am Rand wegtrimmen
        Assert.Equal("trim", StrmSyncEngine.SanitizeTitle("???trim???"));
    }

    // === PrecacheState-aware tests (T05) ===

    /// <summary>
    /// Helper: Creates a PrecacheStateStore backed by a temp directory.
    /// </summary>
    private static (PrecacheStateStore Store, string DataDir) CreateStateStore()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"openmedia-precache-state-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var logger = NullLogger<PrecacheStateStore>.Instance;
        var store = new PrecacheStateStore(dataDir, logger);
        return (store, dataDir);
    }

    /// <summary>
    /// Helper: Marks a hash as cached with a local file.
    /// </summary>
    private static async Task MarkAsCachedAsync(PrecacheStateStore store, string hash, string localPath, CancellationToken ct)
    {
        await store.UpdateAsync(hash, _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 1024,
            SizeBytes = 1024,
            LocalPath = localPath,
            Sha256 = hash,
        }, ct);
    }

    [Fact]
    public async Task SyncAsync_CachedHash_SkipsStrmWrite()
    {
        // (a) cached=true → .strm NICHT geschrieben
        var (store, stateDir) = CreateStateStore();
        using var __ = store;
        try
        {
            // Erzeuge eine fake lokale Datei damit IsCached true wird
            var cachedFile = Path.Combine(_tempDir, $"{HashA}.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(cachedFile)!);
            await File.WriteAllTextAsync(cachedFile, "fake video data");

            await MarkAsCachedAsync(store, HashA, cachedFile, CancellationToken.None);

            var items = new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) };

            var result = await StrmSyncEngine.SyncAsync(
                _tempDir, items, BaseUrl, Token, store,
                NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, result.Added);
            // Skipped=1 (cached) + der skipped counter wird incrementiert
            Assert.True(result.Skipped >= 1, $"Expected at least 1 skipped, got {result.Skipped}");

            // .strm darf NICHT existieren
            var folder = FolderFor("Alpha", 2020, 1L);
            var strmPath = Path.Combine(_tempDir, folder, $"{HashA}.strm");
            Assert.False(File.Exists(strmPath), $"Expected .strm to NOT exist at {strmPath}");
        }
        finally
        {
            try { Directory.Delete(stateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SyncAsync_CachedHash_DeletesExistingStrm()
    {
        // (b) cached=true + existierende .strm → .strm gelöscht
        var (store, stateDir) = CreateStateStore();
        using var __ = store;
        try
        {
            // Erstelle .strm vorher
            var folder = FolderFor("Alpha", 2020, 1L);
            var folderPath = Path.Combine(_tempDir, folder);
            Directory.CreateDirectory(folderPath);
            var strmPath = Path.Combine(folderPath, $"{HashA}.strm");
            await File.WriteAllTextAsync(strmPath, $"{BaseUrl}/jellyfin/stream/{HashA}?token={Token}");

            // Erzeuge .mp4 cached file
            var cachedFile = Path.Combine(_tempDir, $"{HashA}.mp4");
            await File.WriteAllTextAsync(cachedFile, "fake video data");

            await MarkAsCachedAsync(store, HashA, cachedFile, CancellationToken.None);

            Assert.True(File.Exists(strmPath), "Precondition: .strm must exist before sync");

            var items = new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) };

            var result = await StrmSyncEngine.SyncAsync(
                _tempDir, items, BaseUrl, Token, store,
                NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, result.Added);
            Assert.True(result.Skipped >= 1);

            // .strm muss geloescht sein
            Assert.False(File.Exists(strmPath), $"Expected .strm to be deleted at {strmPath}");
        }
        finally
        {
            try { Directory.Delete(stateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SyncAsync_NotCached_WritesStrmNormally()
    {
        // (c) cached=false → normaler .strm-Pfad weiter funktional (Regression-Schutz)
        var (store, stateDir) = CreateStateStore();
        using var __ = store;
        try
        {
            // HashA ist NICHT im PrecacheState → IsCached=false
            var items = new[] { Item(HashA, tmdbId: 1L, title: "Alpha", year: 2020) };

            var result = await StrmSyncEngine.SyncAsync(
                _tempDir, items, BaseUrl, Token, store,
                NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, result.Added);

            var folder = FolderFor("Alpha", 2020, 1L);
            var strmPath = Path.Combine(_tempDir, folder, $"{HashA}.strm");
            Assert.True(File.Exists(strmPath), $"Expected .strm to exist at {strmPath}");
            Assert.Equal($"{BaseUrl}/jellyfin/stream/{HashA}?token={Token}", File.ReadAllText(strmPath));
        }
        finally
        {
            try { Directory.Delete(stateDir, recursive: true); } catch { }
        }
    }
}
