using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Prueft ob genug Speicherplatz auf dem Target-Volume verfuegbar ist,
/// bevor ein Pre-Cache-Download gestartet wird. Verhindert verschwendete
/// Bandbreite und halbe .partial-Files durch proactive Disk-Quota-Pruefung.
/// </summary>
public static class DiskQuotaChecker
{
    /// <summary>
    /// Default Safety-Margin: 20 GB freier Speicher muss nach dem Download
    /// noch uebrig bleiben.
    /// </summary>
    public const long DefaultSafetyMarginBytes = 20L * 1024 * 1024 * 1024; // 20 GB

    /// <summary>
    /// Prueft ob das Target-Volume genug freien Speicher hat.
    /// </summary>
    /// <param name="folder">Target-Ordner (wird auf Root aufgeloestst).</param>
    /// <param name="expectedSize">Erwartete Dateigroesse in Bytes.</param>
    /// <param name="safetyMarginBytes">Safety-Margin in Bytes (Default: 20 GB).</param>
    /// <returns>Tuple mit (ok, freeBytes, requiredBytes). ok=true wenn free >= required + margin.</returns>
    public static (bool Ok, long FreeBytes, long RequiredBytes) CheckTargetFolder(
        string folder,
        long expectedSize,
        long safetyMarginBytes = DefaultSafetyMarginBytes)
    {
        try
        {
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root))
            {
                // Cannot determine root — conservative: not ok
                return (false, 0, expectedSize + safetyMarginBytes);
            }

            // Debug-Override: OM_FAKE_FREE_BYTES allows integration testing
            // of insufficient_disk without actually filling the disk.
            var fakeBytesStr = Environment.GetEnvironmentVariable("OM_FAKE_FREE_BYTES");
            long freeBytes;
            if (!string.IsNullOrEmpty(fakeBytesStr) && long.TryParse(fakeBytesStr, out var fakeBytes))
            {
                freeBytes = fakeBytes;
            }
            else
            {
                var drive = new DriveInfo(root);
                freeBytes = drive.AvailableFreeSpace;
            }

            var requiredBytes = expectedSize + safetyMarginBytes;
            var ok = freeBytes >= requiredBytes;

            return (ok, freeBytes, requiredBytes);
        }
        catch (Exception)
        {
            // Manche Filesystems oder Container werfen bei DriveInfo.
            // Conservative: not ok when we cannot determine free space.
            return (false, 0, expectedSize + safetyMarginBytes);
        }
    }
}
