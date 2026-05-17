using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Ergebnis eines erfolgreichen Downloads.
/// </summary>
public sealed record DownloadResult(
    string Sha256,
    string FinalPath,
    long BytesDownloaded);

/// <summary>
/// Download-Mechanik fuer den Pre-Cache-Worker. Unterstuetzt Range-Resume,
/// SHA256-Verifikation und atomic Rename. Iso vom HostedService-Polling
/// entwickelt und getestet.
/// </summary>
public sealed class PrecacheDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Chunk-Groesse fuer Stream-Leseoperationen: 1 MB.</summary>
    private const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// Progress-Report-Intervall: meldet alle verarbeiteten Bytes.
    /// Caller steuert Frequenz ueber IProgress-Implementierung.
    /// </summary>
    private const long ProgressReportInterval = ChunkSize;

    public PrecacheDownloader(HttpClient http, ILogger<PrecacheDownloader> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Laedt eine Datei von <paramref name="signedUrl"/> herunter mit Range-Resume,
    /// SHA256-Verifikation und atomic Rename.
    /// </summary>
    /// <param name="hash">Hash-Identifikator (wird Teil des Dateinamens).</param>
    /// <param name="signedUrl">Signierte S3-URL (HTTP-302 Target aus S02).</param>
    /// <param name="expectedSize">Erwartete Dateigroesse in Bytes.</param>
    /// <param name="targetFolder">Zielordner fuer die heruntergeladene Datei.</param>
    /// <param name="progress">Progress-Callback fuer verarbeitete Bytes.</param>
    /// <param name="ct">Cancellation-Token.</param>
    /// <returns>DownloadResult mit SHA256, FinalPath und BytesDownloaded.</returns>
    public async Task<DownloadResult> DownloadAsync(
        string hash,
        string signedUrl,
        long expectedSize,
        string targetFolder,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(signedUrl);
        ArgumentNullException.ThrowIfNull(targetFolder);

        if (expectedSize <= 0)
        {
            throw new ArgumentException($"ExpectedSize must be > 0, got {expectedSize}", nameof(expectedSize));
        }

        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(targetFolder);

        var partialPath = Path.Combine(targetFolder, $"{hash}.mp4.partial");
        var finalPath = Path.Combine(targetFolder, $"{hash}.mp4");

        // Falls die finale Datei bereits existiert → direkt OK
        if (File.Exists(finalPath))
        {
            var existingSize = new FileInfo(finalPath).Length;
            if (existingSize == expectedSize)
            {
                _logger.LogInformation("precache:already_complete {Hash} {Path}", hash, finalPath);
                var existingSha = await ComputeFileSha256Async(finalPath, ct).ConfigureAwait(false);
                return new DownloadResult(existingSha, finalPath, 0);
            }
        }

        // Pruefe ob .partial existiert → Resume
        long resumeFrom = 0;
        if (File.Exists(partialPath))
        {
            resumeFrom = new FileInfo(partialPath).Length;
        }

        _logger.LogInformation(
            "precache:download_start {Hash} {SizeBytes} {ResumeFrom}",
            hash, expectedSize, resumeFrom);

        // HTTP-Request mit Range-Header
        using var request = new HttpRequestMessage(HttpMethod.Get, signedUrl);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var isResume = response.StatusCode == System.Net.HttpStatusCode.PartialContent; // 206
        long totalBytesToRead;
        long startOffset;

        if (isResume)
        {
            // Server unterstuetzt Range → append an .partial
            startOffset = resumeFrom;
            totalBytesToRead = resumeFrom + response.Content.Headers.ContentLength.GetValueOrDefault(0);
        }
        else
        {
            // Server ignoriert Range (200) → truncate .partial und start over
            if (resumeFrom > 0)
            {
                _logger.LogInformation("precache:range_ignored {Hash} — starting from beginning", hash);
            }

            startOffset = 0;
            totalBytesToRead = response.Content.Headers.ContentLength ?? expectedSize;
        }

        // SHA256: bei Resume muessen die existierenden Bytes gehasht werden,
        // BEVOR die Datei zum Append geoeffnet wird (FileShare-Konflikt vermeiden).
        using var sha256 = SHA256.Create();

        if (isResume && startOffset > 0)
        {
            await using var readExisting = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
            var existingBuffer = new byte[ChunkSize];
            int bytesRead;
            while ((bytesRead = await readExisting.ReadAsync(existingBuffer, ct).ConfigureAwait(false)) > 0)
            {
                sha256.TransformBlock(existingBuffer, 0, bytesRead, existingBuffer, 0);
            }
        }

        // Oeffne .partial zum Schreiben
        var fileMode = isResume ? FileMode.Append : FileMode.Create;
        var fileAccess = FileAccess.Write;
        var fileShare = FileShare.None;

        await using var fileStream = new FileStream(partialPath, fileMode, fileAccess, fileShare, ChunkSize, useAsync: true);

        // Lese Response-Body in Chunks
        await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[ChunkSize];
        long totalBytesRead = startOffset;
        long bytesSinceLastReport = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var read = await responseStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

            sha256.TransformBlock(buffer, 0, read, buffer, 0);

            totalBytesRead += read;
            bytesSinceLastReport += read;

            if (bytesSinceLastReport >= ProgressReportInterval)
            {
                progress?.Report(totalBytesRead);

                var percent = (int)((totalBytesRead * 100) / Math.Max(totalBytesToRead, 1));
                _logger.LogDebug(
                    "precache:download_progress {Hash} {Percent}% {BytesSoFar}/{TotalBytes}",
                    hash, percent, totalBytesRead, totalBytesToRead);

                bytesSinceLastReport = 0;
            }
        }

        // Final progress report
        progress?.Report(totalBytesRead);

        // Flush before verification
        await fileStream.FlushAsync(ct).ConfigureAwait(false);
        fileStream.Close();

        // SHA256 Finalize
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hashBytes = sha256.Hash!;
        var sha256String = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

        // Size verification
        var actualSize = new FileInfo(partialPath).Length;
        if (actualSize != expectedSize)
        {
            _logger.LogError(
                "precache:size_mismatch {Hash} expected={Expected} actual={Actual}",
                hash, expectedSize, actualSize);

            throw new IOException(
                $"Size mismatch for {hash}: expected {expectedSize} bytes, got {actualSize} bytes. Partial file preserved at {partialPath}");
        }

        _logger.LogInformation("precache:sha_verify {Hash} {Sha256} ok=true", hash, sha256String);

        // Atomic rename
        File.Move(partialPath, finalPath, overwrite: true);

        _logger.LogInformation("precache:atomic_rename {Hash} {FinalPath}", hash, finalPath);

        return new DownloadResult(sha256String, finalPath, totalBytesRead - startOffset);
    }

    /// <summary>
    /// Berechnet den SHA256-Hash einer existierenden Datei.
    /// </summary>
    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
        var hashBytes = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }
}
