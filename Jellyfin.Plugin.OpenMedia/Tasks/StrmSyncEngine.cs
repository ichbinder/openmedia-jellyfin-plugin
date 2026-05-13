using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.OpenMedia.Api;

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
/// Schreibt {hash}.strm-Dateien fuer alle Library-Items mit gueltigem Hash + TmdbId,
/// loescht eigene STRMs deren Hash nicht mehr in der Library steht, laesst fremde Dateien in Ruhe.
/// Idempotent: identischer Content → kein Write, kein Mtime-Update.
/// </summary>
public static class StrmSyncEngine
{
    /// <summary>
    /// Regex fuer einen gueltigen Sync-Hash: exakt 64 hex-Lowercase-Zeichen.
    /// Verhindert das Loeschen von z.B. "backup-2024.strm".
    /// </summary>
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Synchronisiert die STRM-Dateien in <paramref name="strmDirectory"/> mit
    /// <paramref name="libraryItems"/>. Erstellt das Verzeichnis bei Bedarf.
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

        int added = 0, updated = 0, unchanged = 0, removed = 0, skipped = 0, rejected = 0;

        var seenHashes = new HashSet<string>(StringComparer.Ordinal);

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

            seenHashes.Add(item.Hash);

            var targetPath = Path.Combine(rootFull, $"{item.Hash}.strm");
            var targetFull = Path.GetFullPath(targetPath);
            if (!targetFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

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

        // Cleanup: nur eigene *.strm Files, deren Filename ein gueltiger Hash ist
        // und die nicht in seenHashes stehen.
        var (cleanupRemoved, foreign) = CleanupStaleStrm(rootFull, rootWithSep, seenHashes);
        removed += cleanupRemoved;

        return new StrmSyncResult(added, updated, unchanged, removed, skipped, foreign, rejected);
    }

    /// <summary>
    /// Baut die Stream-URL fuer einen Hash. Internal fuer Tests.
    /// </summary>
    internal static string BuildStreamUrl(string baseUrl, string hash, string token)
    {
        var encodedToken = Uri.EscapeDataString(token);
        return $"{baseUrl}/jellyfin/stream/{hash}?token={encodedToken}";
    }

    private static (int Removed, int Foreign) CleanupStaleStrm(
        string rootFull,
        string rootWithSep,
        HashSet<string> seenHashes)
    {
        int removed = 0;
        int foreign = 0;

        // EnumerateFiles mit "*.strm" Filter — fremde Endungen werden gar nicht aufgezaehlt.
        // Foreign-Counter zaehlt explizit alle Nicht-.strm Dateien fuer Diagnose-Zwecke.
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

            if (seenHashes.Contains(hash))
            {
                continue;
            }

            // Path-Safety: re-check dass die Datei wirklich unter rootFull liegt
            // (z.B. gegen Symlink-Tricks).
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                continue;
            }

            File.Delete(fullPath);
            removed++;
        }

        return (removed, foreign);
    }
}
