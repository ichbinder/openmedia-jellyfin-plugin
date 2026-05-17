using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Ergebnis-Counter eines Sync-Laufs.
/// </summary>
public sealed record StrmSyncResult(
    int Added,
    int Updated,
    int Unchanged,
    int Removed,
    int Skipped,
    int Foreign,
    int Rejected);

/// <summary>
/// Reine STRM-Sync-Logik ohne Jellyfin-Dependencies — kann gegen ein TempDir getestet werden.
/// Legt pro Library-Item einen Ordner <c>{Title}[ ({Year})] [tmdbid-N]</c> an und schreibt
/// darin eine einzige <c>{hash}.strm</c> Datei. Dadurch erkennt Jellyfins TMDB-Provider
/// den Film automatisch und holt Poster, Plot, Cast etc.
///
/// Inkrementelles Verhalten:
///  * existiert Ordner + Datei mit identischem URL-Inhalt -> nicht angefasst (mtime bleibt).
///  * existiert Ordner + Datei mit anderem URL-Inhalt -> nur die Datei ueberschrieben (updated).
///  * neuer Hash -> Ordner + Datei angelegt (added).
///  * Hash nicht mehr in Library, oder Titel/Jahr geaendert (= anderer Ordnername)
///    -> alte Datei + ggf. leerer alter Ordner entfernt (removed).
///  * Legacy flat <c>{hash}.strm</c> im Root (Vorgaengerversion ohne Ordner) -> entfernt (removed).
///  * Alles andere im Verzeichnis (fremde Dateien, fremde Ordner, .strm mit Nicht-Hash-Namen,
///    Ordner ohne tmdbid-Suffix) -> nicht angefasst, nur als <c>Foreign</c> gezaehlt.
/// </summary>
public static class StrmSyncEngine
{
    /// <summary>
    /// Regex fuer einen gueltigen Sync-Hash: exakt 64 hex-Lowercase-Zeichen.
    /// Verhindert das Loeschen von z.B. "backup-2024.strm".
    /// </summary>
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Erkennt von uns erzeugte Ordner: irgendwas, gefolgt von " [tmdbid-N]" am Ende.
    /// </summary>
    private static readonly Regex TmdbFolderPattern = new(@" \[tmdbid-\d+\]$", RegexOptions.Compiled);

    /// <summary>
    /// Erlaubte Zeichen in einem sanitisierten Titel — alles andere wird durch Space ersetzt.
    /// </summary>
    private static readonly Regex AllowedTitleChars = new(@"[^A-Za-z0-9 _.,!&'()\-]", RegexOptions.Compiled);

    private static readonly Regex MultipleSpaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Synchronisiert das STRM-Verzeichnis mit <paramref name="libraryItems"/>.
    /// Erstellt das Wurzelverzeichnis bei Bedarf.
    /// </summary>
    /// <param name="strmDirectory">Zielverzeichnis (absoluter Pfad).</param>
    /// <param name="libraryItems">Aktuelle API-Library.</param>
    /// <param name="apiBaseUrl">Basis-URL fuer die STRM-URL, ohne Trailing-Slash.</param>
    /// <param name="apiToken">om_-Token fuer den ?token= Query-Parameter.</param>
    public static StrmSyncResult Sync(
        string strmDirectory,
        IEnumerable<LibraryItem> libraryItems,
        string apiBaseUrl,
        string apiToken)
    {
        if (string.IsNullOrWhiteSpace(strmDirectory))
        {
            throw new ArgumentException("StrmDirectory ist leer.", nameof(strmDirectory));
        }

        Directory.CreateDirectory(strmDirectory);

        // Canonical root — alle nachfolgenden File-Ops muessen darunter bleiben.
        var rootFull = Path.GetFullPath(strmDirectory);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        var baseUrl = (apiBaseUrl ?? string.Empty).TrimEnd('/');
        var token = apiToken ?? string.Empty;

        int added = 0, updated = 0, unchanged = 0, skipped = 0, rejected = 0;

        // hash -> erwarteter Ordnername (relativ zu rootFull)
        var expectedFolderByHash = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in libraryItems)
        {
            if (string.IsNullOrWhiteSpace(item.Hash) || item.TmdbId is null)
            {
                skipped++;
                continue;
            }

            if (!HashPattern.IsMatch(item.Hash))
            {
                rejected++;
                continue;
            }

            var folderName = BuildFolderName(item.Title, item.Year, item.TmdbId.Value);

            var folderPath = Path.Combine(rootFull, folderName);
            var folderFull = Path.GetFullPath(folderPath);
            if (!folderFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            var targetPath = Path.Combine(folderFull, $"{item.Hash}.strm");
            var targetFull = Path.GetFullPath(targetPath);
            if (!targetFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            expectedFolderByHash[item.Hash] = folderName;

            Directory.CreateDirectory(folderFull);

            var url = BuildStreamUrl(baseUrl, item.Hash, token);

            if (File.Exists(targetFull))
            {
                var existing = File.ReadAllText(targetFull);
                if (string.Equals(existing, url, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                File.WriteAllText(targetFull, url);
                updated++;
            }
            else
            {
                File.WriteAllText(targetFull, url);
                added++;
            }
        }

        var (cleanupRemoved, foreign) = CleanupStale(rootFull, rootWithSep, expectedFolderByHash);

        return new StrmSyncResult(added, updated, unchanged, cleanupRemoved, skipped, foreign, rejected);
    }

    /// <summary>
    /// Async Variante mit Pre-Cache-State-Check. Skippt .strm-Schreiben fuer Hashes
    /// die bereits als cached markiert sind (PrecacheWorker hat .mp4 daneben gelegt).
    /// Loescht existierende .strm wenn Hash als cached gilt.
    /// Single Source of Truth gegen Race-Conditions mit PrecacheWorker.
    /// </summary>
    public static async Task<StrmSyncResult> SyncAsync(
        string strmDirectory,
        IEnumerable<LibraryItem> libraryItems,
        string apiBaseUrl,
        string apiToken,
        PrecacheStateStore precacheState,
        ILogger? logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(precacheState);

        if (string.IsNullOrWhiteSpace(strmDirectory))
        {
            throw new ArgumentException("StrmDirectory ist leer.", nameof(strmDirectory));
        }

        Directory.CreateDirectory(strmDirectory);

        var rootFull = Path.GetFullPath(strmDirectory);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        var baseUrl = (apiBaseUrl ?? string.Empty).TrimEnd('/');
        var token = apiToken ?? string.Empty;

        int added = 0, updated = 0, unchanged = 0, skipped = 0, rejected = 0;

        var expectedFolderByHash = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in libraryItems)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(item.Hash) || item.TmdbId is null)
            {
                skipped++;
                continue;
            }

            if (!HashPattern.IsMatch(item.Hash))
            {
                rejected++;
                continue;
            }

            var folderName = BuildFolderName(item.Title, item.Year, item.TmdbId.Value);

            var folderPath = Path.Combine(rootFull, folderName);
            var folderFull = Path.GetFullPath(folderPath);
            if (!folderFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            var targetPath = Path.Combine(folderFull, $"{item.Hash}.strm");
            var targetFull = Path.GetFullPath(targetPath);
            if (!targetFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            expectedFolderByHash[item.Hash] = folderName;

            // Pre-Cache-Check: wenn Hash als cached gilt → .strm nicht schreiben
            var isCached = await precacheState.IsCachedAsync(item.Hash, ct).ConfigureAwait(false);
            if (isCached)
            {
                logger?.LogInformation("strm_sync:skipped_cached {Hash}", item.Hash);

                // Wenn .strm existiert → loeschen (Cleanup: PrecacheWorker hat .mp4 daneben)
                if (File.Exists(targetFull))
                {
                    File.Delete(targetFull);
                    logger?.LogInformation("strm_sync:deleted_cached_strm {Hash} {Path}", item.Hash, targetFull);
                }

                skipped++;
                continue;
            }

            Directory.CreateDirectory(folderFull);

            var url = BuildStreamUrl(baseUrl, item.Hash, token);

            if (File.Exists(targetFull))
            {
                var existing = File.ReadAllText(targetFull);
                if (string.Equals(existing, url, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                File.WriteAllText(targetFull, url);
                updated++;
            }
            else
            {
                File.WriteAllText(targetFull, url);
                added++;
            }
        }

        var (cleanupRemoved, foreign) = CleanupStale(rootFull, rootWithSep, expectedFolderByHash);

        return new StrmSyncResult(added, updated, unchanged, cleanupRemoved, skipped, foreign, rejected);
    }

    /// <summary>
    /// Baut die Stream-URL fuer einen Hash. Internal fuer Tests.
    /// </summary>
    internal static string BuildStreamUrl(string baseUrl, string hash, string token)
    {
        var encodedToken = Uri.EscapeDataString(token);
        return $"{baseUrl}/jellyfin/stream/{hash}?token={encodedToken}";
    }

    /// <summary>
    /// Baut den Ordnernamen <c>{SanitizedTitle}[ ({Year})] [tmdbid-N]</c>.
    /// Faellt auf <c>"Untitled"</c> zurueck wenn der Titel nach Sanitisierung leer ist.
    /// Internal fuer Tests.
    /// </summary>
    internal static string BuildFolderName(string? title, int? year, long tmdbId)
    {
        var sanitized = SanitizeTitle(title);
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "Untitled";
        }

        var yearPart = year.HasValue ? $" ({year.Value})" : string.Empty;
        return $"{sanitized}{yearPart} [tmdbid-{tmdbId}]";
    }

    /// <summary>
    /// Ersetzt alle Zeichen ausserhalb von <c>[A-Za-z0-9 _.,!&amp;'()-]</c> durch Space,
    /// kollabiert mehrfache Whitespaces und trimmt. Internal fuer Tests.
    /// </summary>
    internal static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var replaced = AllowedTitleChars.Replace(title, " ");
        var collapsed = MultipleSpaces.Replace(replaced, " ").Trim();
        return collapsed;
    }

    private static (int Removed, int Foreign) CleanupStale(
        string rootFull,
        string rootWithSep,
        Dictionary<string, string> expectedFolderByHash)
    {
        int removed = 0;
        int foreign = 0;

        // 1) Legacy flat *.strm direkt im Root (Vorgaengerversion ohne Ordner).
        //    Eigene Hash-Namen -> loeschen, fremde Dateien -> als Foreign zaehlen.
        foreach (var path in Directory.EnumerateFiles(rootFull))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                foreign++;
                continue;
            }

            var hash = Path.GetFileNameWithoutExtension(name);
            if (!HashPattern.IsMatch(hash))
            {
                // .strm Datei mit ungueltigem Hash-Namen → nicht von uns, nicht anfassen.
                foreign++;
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                continue;
            }

            File.Delete(fullPath);
            removed++;
        }

        // 2) Unterordner verarbeiten. Nur Ordner die unserem Pattern entsprechen
        //    (Suffix " [tmdbid-N]") werden ueberhaupt angefasst.
        foreach (var dirPath in Directory.EnumerateDirectories(rootFull))
        {
            var dirName = Path.GetFileName(dirPath);
            if (!TmdbFolderPattern.IsMatch(dirName))
            {
                // Fremder Ordner — komplett in Ruhe lassen.
                foreign++;
                continue;
            }

            var dirFull = Path.GetFullPath(dirPath);
            if (!dirFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                continue;
            }

            // STRM-Dateien innerhalb des Ordners pruefen.
            foreach (var strmPath in Directory.EnumerateFiles(dirFull, "*.strm"))
            {
                var strmName = Path.GetFileName(strmPath);
                var hash = Path.GetFileNameWithoutExtension(strmName);
                if (!HashPattern.IsMatch(hash))
                {
                    // Fremde .strm in unserem Ordner — vorsichtshalber nicht anfassen.
                    continue;
                }

                if (expectedFolderByHash.TryGetValue(hash, out var expectedFolder)
                    && string.Equals(expectedFolder, dirName, StringComparison.Ordinal))
                {
                    // Hash gehoert genau hierher. Bleibt.
                    continue;
                }

                // Hash nicht mehr in Library ODER in einen anderen Ordner gewandert.
                var strmFull = Path.GetFullPath(strmPath);
                if (!strmFull.StartsWith(rootWithSep, StringComparison.Ordinal))
                {
                    continue;
                }

                File.Delete(strmFull);
                removed++;
            }

            // Ordner leer? Dann auch entfernen.
            if (!Directory.EnumerateFileSystemEntries(dirFull).Any())
            {
                Directory.Delete(dirFull);
            }
        }

        return (removed, foreign);
    }
}
