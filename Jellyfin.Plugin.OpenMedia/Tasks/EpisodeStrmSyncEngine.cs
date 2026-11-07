using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Schreibt Folgen-STRM-Dateien im Jellyfin-TV-Layout
/// <c>{Show} ({Year}) [tmdbid-N]/Season NN/{Show} SNNENN.strm</c>.
/// Geloescht werden nur .strm-Dateien unterhalb von <c>[tmdbid-N]</c>-Ordnern, deren Inhalt
/// mit der eigenen Stream-URL beginnt.
/// </summary>
public static class EpisodeStrmSyncEngine
{
    public static async Task<StrmSyncResult> SyncAsync(
        string tvDirectory,
        IEnumerable<LibraryItem> episodes,
        string apiBaseUrl,
        string apiToken,
        PrecacheStateStore? precacheState,
        ILogger? logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tvDirectory))
        {
            throw new ArgumentException("TvDirectory ist leer.", nameof(tvDirectory));
        }

        Directory.CreateDirectory(tvDirectory);

        var rootFull = Path.GetFullPath(tvDirectory);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        var baseUrl = (apiBaseUrl ?? string.Empty).TrimEnd('/');
        var token = apiToken ?? string.Empty;
        var ownedUrlPrefix = $"{baseUrl}/jellyfin/stream/";

        int added = 0, updated = 0, unchanged = 0, skipped = 0, rejected = 0;
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in episodes)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.Hash) || item.TmdbId is null
                || item.Season is null || item.Episode is null)
            {
                skipped++;
                continue;
            }

            if (!StrmSyncEngine.HashPattern.IsMatch(item.Hash))
            {
                rejected++;
                continue;
            }

            var relativePath = BuildEpisodeRelativePath(
                item.Title, item.Year, item.TmdbId.Value, item.Season.Value, item.Episode.Value);
            var targetFull = Path.GetFullPath(Path.Combine(rootFull, relativePath));
            if (!targetFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            expectedPaths.Add(targetFull);

            if (precacheState is not null && await precacheState.IsCachedAsync(item.Hash, ct).ConfigureAwait(false))
            {
                logger?.LogInformation("episode_strm_sync:skipped_cached {Hash}", item.Hash);
                if (File.Exists(targetFull))
                {
                    File.Delete(targetFull);
                }

                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
            var url = StrmSyncEngine.BuildStreamUrl(baseUrl, item.Hash, token);

            if (!File.Exists(targetFull))
            {
                File.WriteAllText(targetFull, url);
                added++;
            }
            else if (string.Equals(File.ReadAllText(targetFull), url, StringComparison.Ordinal))
            {
                unchanged++;
            }
            else
            {
                File.WriteAllText(targetFull, url);
                updated++;
            }
        }

        var (removed, foreign) = CleanupStale(rootFull, ownedUrlPrefix, expectedPaths);
        return new StrmSyncResult(added, updated, unchanged, removed, skipped, foreign, rejected);
    }

    internal static string BuildEpisodeRelativePath(string? showTitle, int? year, long tmdbId, int season, int episode)
    {
        var name = StrmSyncEngine.SanitizeTitle(showTitle);
        if (string.IsNullOrEmpty(name))
        {
            name = "Untitled";
        }

        return Path.Combine(
            StrmSyncEngine.BuildFolderName(showTitle, year, tmdbId),
            $"Season {season:00}",
            $"{name} S{season:00}E{episode:00}.strm");
    }

    private static (int Removed, int Foreign) CleanupStale(
        string rootFull,
        string ownedUrlPrefix,
        HashSet<string> expectedPaths)
    {
        int removed = 0, foreign = 0;

        foreign += Directory.EnumerateFiles(rootFull).Count();

        foreach (var showDir in Directory.EnumerateDirectories(rootFull))
        {
            if (!StrmSyncEngine.TmdbFolderPattern.IsMatch(Path.GetFileName(showDir)))
            {
                foreign++;
                continue;
            }

            foreach (var strmPath in Directory.EnumerateFiles(showDir, "*.strm", SearchOption.AllDirectories))
            {
                var strmFull = Path.GetFullPath(strmPath);
                if (expectedPaths.Contains(strmFull))
                {
                    continue;
                }

                if (!File.ReadAllText(strmFull).StartsWith(ownedUrlPrefix, StringComparison.Ordinal))
                {
                    foreign++;
                    continue;
                }

                File.Delete(strmFull);
                removed++;
            }

            foreach (var seasonDir in Directory.EnumerateDirectories(showDir))
            {
                DeleteIfEmpty(seasonDir);
            }

            DeleteIfEmpty(showDir);
        }

        return (removed, foreign);
    }

    private static void DeleteIfEmpty(string dir)
    {
        if (!Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
        }
    }
}
