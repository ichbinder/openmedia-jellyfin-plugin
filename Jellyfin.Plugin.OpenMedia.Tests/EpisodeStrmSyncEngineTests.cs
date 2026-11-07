using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class EpisodeStrmSyncEngineTests : IDisposable
{
    private readonly string _tempDir;
    private const string BaseUrl = "https://api.example.com";
    private const string Token = "om_test_token";
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public EpisodeStrmSyncEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-episode-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static LibraryItem Episode(
        string hash,
        string? title = "Severance",
        int? year = 2022,
        long? tmdbId = 95396L,
        int? season = 2,
        int? episode = 5) =>
        new(hash, tmdbId, title, year, "1000", 45, "1080p", "episode", season, episode);

    private static string Url(string hash) => $"{BaseUrl}/jellyfin/stream/{hash}?token={Token}";

    private Task<StrmSyncResult> SyncAsync(params LibraryItem[] items) =>
        EpisodeStrmSyncEngine.SyncAsync(_tempDir, items, BaseUrl, Token, null, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task WritesEpisodeStrmInSeasonFolder()
    {
        var result = await SyncAsync(Episode(HashA));

        var path = Path.Combine(_tempDir, "Severance (2022) [tmdbid-95396]", "Season 02", "Severance S02E05.strm");
        Assert.Equal(Url(HashA), File.ReadAllText(path));
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public async Task VersionChangeKeepsFileNameAndRewritesContent()
    {
        await SyncAsync(Episode(HashA));
        var result = await SyncAsync(Episode(HashB));

        var path = Path.Combine(_tempDir, "Severance (2022) [tmdbid-95396]", "Season 02", "Severance S02E05.strm");
        Assert.Equal(Url(HashB), File.ReadAllText(path));
        Assert.Equal(1, result.Updated);
        Assert.Single(Directory.GetFiles(_tempDir, "*.strm", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RemovesEpisodeAndEmptySeasonAndShowFolders()
    {
        await SyncAsync(Episode(HashA));
        var result = await SyncAsync();

        Assert.Equal(1, result.Removed);
        Assert.Empty(Directory.GetFileSystemEntries(_tempDir));
    }

    [Fact]
    public async Task RemovesOnlyTheLeftEpisodeAndKeepsSiblings()
    {
        await SyncAsync(Episode(HashA), Episode(HashB, episode: 6));
        await SyncAsync(Episode(HashB, episode: 6));

        var season = Path.Combine(_tempDir, "Severance (2022) [tmdbid-95396]", "Season 02");
        Assert.False(File.Exists(Path.Combine(season, "Severance S02E05.strm")));
        Assert.True(File.Exists(Path.Combine(season, "Severance S02E06.strm")));
    }

    [Fact]
    public async Task LeavesForeignFilesUntouched()
    {
        await SyncAsync(Episode(HashA));
        var season = Path.Combine(_tempDir, "Severance (2022) [tmdbid-95396]", "Season 02");
        var foreignStrm = Path.Combine(season, "Other S02E09.strm");
        var notes = Path.Combine(season, "notes.txt");
        File.WriteAllText(foreignStrm, "https://elsewhere.example/video");
        File.WriteAllText(notes, "keep");
        var foreignDir = Path.Combine(_tempDir, "My Own Show");
        Directory.CreateDirectory(foreignDir);
        File.WriteAllText(Path.Combine(foreignDir, "x.strm"), "https://elsewhere.example/x");

        await SyncAsync();

        Assert.True(File.Exists(foreignStrm));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(Path.Combine(foreignDir, "x.strm")));
        Assert.False(File.Exists(Path.Combine(season, "Severance S02E05.strm")));
    }

    [Fact]
    public async Task SanitizesSpecialCharactersStably()
    {
        var item = Episode(HashA, title: "Star Trek: Discovery", year: 2017, tmdbId: 67198, season: 1, episode: 1);

        await SyncAsync(item);
        var second = await SyncAsync(item);

        var path = Path.Combine(_tempDir, "Star Trek Discovery (2017) [tmdbid-67198]", "Season 01", "Star Trek Discovery S01E01.strm");
        Assert.True(File.Exists(path));
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.Added + second.Updated + second.Removed);
    }

    [Fact]
    public async Task SkipsItemsWithoutSeasonOrEpisodeOrTmdbId()
    {
        var result = await SyncAsync(
            Episode(HashA, season: null),
            Episode(HashA, episode: null),
            Episode(HashA, tmdbId: null));

        Assert.Equal(3, result.Skipped);
        Assert.Empty(Directory.GetFileSystemEntries(_tempDir));
    }

    [Fact]
    public async Task RejectsInvalidHashes()
    {
        var result = await SyncAsync(Episode("not-a-hash"), Episode("../../evil"));

        Assert.Equal(2, result.Rejected);
        Assert.Empty(Directory.GetFileSystemEntries(_tempDir));
    }

    [Fact]
    public async Task SkipsCachedHash()
    {
        var stateDir = Path.Combine(_tempDir, "state");
        using var store = new PrecacheStateStore(stateDir, NullLogger<PrecacheStateStore>.Instance);
        var cachedFile = Path.Combine(stateDir, "cached.mp4");
        File.WriteAllText(cachedFile, "fake video data");
        await store.UpdateAsync(HashA, _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 1024,
            SizeBytes = 1024,
            LocalPath = cachedFile,
            Sha256 = HashA,
        }, CancellationToken.None);
        var tvDir = Path.Combine(_tempDir, "tv");

        var result = await EpisodeStrmSyncEngine.SyncAsync(
            tvDir, new[] { Episode(HashA) }, BaseUrl, Token, store, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(Directory.GetFiles(tvDir, "*.strm", SearchOption.AllDirectories));
    }

    private string TargetPath() =>
        Path.Combine(_tempDir, "Severance (2022) [tmdbid-95396]", "Season 02", "Severance S02E05.strm");

    private string WriteAtTarget(string content)
    {
        var path = TargetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<StrmSyncResult> SyncCachedAsync()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"openmedia-episode-state-{Guid.NewGuid():N}");
        using var store = new PrecacheStateStore(stateDir, NullLogger<PrecacheStateStore>.Instance);
        var cachedFile = Path.Combine(Directory.CreateDirectory(stateDir).FullName, "cached.mp4");
        File.WriteAllText(cachedFile, "fake video data");
        await store.UpdateAsync(HashA, _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 1024,
            SizeBytes = 1024,
            LocalPath = cachedFile,
            Sha256 = HashA,
        }, CancellationToken.None);
        return await EpisodeStrmSyncEngine.SyncAsync(
            _tempDir, new[] { Episode(HashA) }, BaseUrl, Token, store, NullLogger.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task CachedHash_KeepsForeignFileAtTargetPath()
    {
        var path = WriteAtTarget("https://elsewhere.example/mine");

        var result = await SyncCachedAsync();

        Assert.Equal("https://elsewhere.example/mine", File.ReadAllText(path));
        Assert.Equal(1, result.Skipped + result.Foreign);
    }

    [Fact]
    public async Task CachedHash_DeletesOwnedStrmAtTargetPath()
    {
        var path = WriteAtTarget(Url(HashA));

        await SyncCachedAsync();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ForeignFileAtTargetPath_IsNotOverwritten()
    {
        var path = WriteAtTarget("https://elsewhere.example/mine");

        var result = await SyncAsync(Episode(HashA));

        Assert.Equal("https://elsewhere.example/mine", File.ReadAllText(path));
        Assert.Equal(0, result.Added + result.Updated);
        Assert.Equal(1, result.Foreign);
    }

    [Fact]
    public async Task DuplicateEpisodeVersions_FirstItemWinsStably()
    {
        var items = new[] { Episode(HashA), Episode(HashB) };

        var first = await SyncAsync(items);
        var second = await SyncAsync(items);

        Assert.Equal(Url(HashA), File.ReadAllText(TargetPath()));
        Assert.Equal(1, first.Added);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public void BuildEpisodeRelativePath_FallsBackToUntitled()
    {
        var path = EpisodeStrmSyncEngine.BuildEpisodeRelativePath("???", null, 7, 1, 2);

        Assert.Equal(Path.Combine("Untitled [tmdbid-7]", "Season 01", "Untitled S01E02.strm"), path);
    }
}
