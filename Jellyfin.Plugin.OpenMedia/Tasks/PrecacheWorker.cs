using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Configuration;
using Jellyfin.Plugin.OpenMedia.MediaSources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// HostedService: orchestriert den vollstaendigen Pre-Cache-Loop.
/// Pollt alle 30s die Queue, signiert URLs via MediaUrlSigner (S02),
/// laedt mit Range-Resume via PrecacheDownloader (T03), verifiziert SHA256,
/// triggert ILibraryManager-Refresh. State persistiert via PrecacheStateStore (T01).
/// Max 1 Download parallel. Bei API-Fehlern: exponential Backoff, kein Worker-Crash.
/// </summary>
public class PrecacheWorker : BackgroundService
{
    /// <summary>Normales Polling-Intervall.</summary>
    internal static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Backoff bei Fehlern (API-Down, Network-Error, 5xx).</summary>
    internal static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);

    private readonly ILogger<PrecacheWorker> _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly PrecacheDownloader? _downloader;
    private readonly PrecacheStateStore _stateStore;
    private readonly ILibraryManager? _libraryManager;
    private readonly IApplicationPaths? _applicationPaths;

    /// <summary>
    /// Produktions-Konstruktor. Alle Dependencies werden via DI injiziert.
    /// </summary>
    public PrecacheWorker(
        ILogger<PrecacheWorker> logger,
        IHttpClientFactory httpClientFactory,
        PrecacheDownloader downloader,
        PrecacheStateStore stateStore,
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
    }

    /// <summary>
    /// Test-Konstruktor — nur Logger und StateStore erforderlich.
    /// Alle externen Operationen gehen durch virtuelle Methoden (in Tests ueberschrieben).
    /// </summary>
    protected PrecacheWorker(ILogger<PrecacheWorker> logger, PrecacheStateStore stateStore)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("precache:worker_started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await TickAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("precache:worker_stopped");
    }

    /// <summary>
    /// Ein einzelner Polling-Tick. Returnt das Delay bis zum naechsten Tick.
    /// Internal damit Tests einen Tick einzeln treiben koennen.
    /// </summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        try
        {
            // (1) Process release queue first — free disk space before new downloads
            await ProcessReleaseQueueAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Release queue failure is non-fatal — log and continue to normal queue
            _logger.LogWarning(ex, "precache:release_queue_poll_failed");
        }

        try
        {
            var queue = await GetQueueAsync(ct).ConfigureAwait(false);

            _logger.LogDebug("precache:worker_poll {Pending}", queue.Count);

            if (queue.Count == 0)
            {
                return PollingInterval;
            }

            // Max 1 item per tick (Technical Constraint)
            var item = queue[0];

            await ProcessItemAsync(item, ct).ConfigureAwait(false);

            return PollingInterval;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "precache:worker_poll_failed");
            return ErrorBackoff;
        }
    }

    private async Task ProcessItemAsync(QueueItem item, CancellationToken ct)
    {
        var hash = item.Hash;

        try
        {
            // (1) Check if already done
            var existing = await _stateStore.GetAsync(hash, ct).ConfigureAwait(false);
            if (existing is not null && existing.State == PrecacheState.Done)
            {
                _logger.LogInformation("precache:already_done {Hash}", hash);
                await ReportStatusAsync(hash, "done", null, existing.SizeBytes, existing.SizeBytes, ct).ConfigureAwait(false);
                return;
            }

            // (2) Resolve library item for metadata (Title, Year, TmdbId, FileSize)
            var libItem = await ResolveLibraryItemAsync(hash, ct).ConfigureAwait(false);
            if (libItem is null)
            {
                _logger.LogWarning("precache:hash_not_in_library {Hash}", hash);
                await ReportStatusAsync(hash, "failed", "hash_not_in_library", null, null, ct).ConfigureAwait(false);
                return;
            }

            if (libItem.TmdbId is null)
            {
                _logger.LogWarning("precache:no_tmdb_id {Hash}", hash);
                await ReportStatusAsync(hash, "failed", "no_tmdb_id", null, null, ct).ConfigureAwait(false);
                return;
            }

            // (4) Target folder: gleiche Library-Ordner-Logik wie StrmSyncEngine
            var strmDir = GetStrmDirectory();
            var folderName = StrmSyncEngine.BuildFolderName(libItem.Title, libItem.Year, libItem.TmdbId.Value);
            var targetFolder = Path.Combine(strmDir, folderName);

            // Parse file size
            var sizeBytes = ParseFileSize(libItem.FileSize);
            if (sizeBytes <= 0)
            {
                _logger.LogWarning("precache:invalid_size {Hash} {FileSize}", hash, libItem.FileSize);
                await ReportStatusAsync(hash, "failed", "invalid_size", null, null, ct).ConfigureAwait(false);
                return;
            }

            // (3) Sign URL via MediaUrlSigner (S02)
            var signedUrl = CreateSignedUrl(hash, item.UserId);

            // (5) Disk-Quota Pre-Check: verify enough space BEFORE downloading
            var diskCheck = CheckDiskQuota(targetFolder, sizeBytes);
            _logger.LogInformation(
                "precache:disk_check {Hash} {FreeBytes} {RequiredBytes} {Ok}",
                hash, diskCheck.FreeBytes, diskCheck.RequiredBytes, diskCheck.Ok);

            if (!diskCheck.Ok)
            {
                _logger.LogWarning(
                    "precache:insufficient_disk {Hash} free={FreeBytes} required={RequiredBytes}",
                    hash, diskCheck.FreeBytes, diskCheck.RequiredBytes);

                await _stateStore.UpdateAsync(hash, _ => new PrecacheEntry
                {
                    State = PrecacheState.Failed,
                    DownloadedBytes = 0,
                    SizeBytes = sizeBytes,
                    LastError = "insufficient_disk",
                }, ct).ConfigureAwait(false);

                await ReportStatusAsync(hash, "failed", "insufficient_disk", null, null, ct).ConfigureAwait(false);
                return;
            }

            // (6) State → downloading + ReportStatus(downloading, bytes=0)
            await _stateStore.UpdateAsync(hash, e => new PrecacheEntry
            {
                State = PrecacheState.Downloading,
                DownloadedBytes = e?.DownloadedBytes ?? 0,
                SizeBytes = sizeBytes,
                LocalPath = Path.Combine(targetFolder, $"{hash}.mp4"),
            }, ct).ConfigureAwait(false);

            await ReportStatusAsync(hash, "downloading", null, 0L, sizeBytes, ct).ConfigureAwait(false);

            // (7) Download with progress callback
            DownloadResult downloadResult;
            try
            {
                downloadResult = await PerformDownloadAsync(
                    hash, signedUrl, sizeBytes, targetFolder, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDiskFullException(ex))
            {
                // ENOSPC mid-download: clean up .partial file, report failure
                var partialPath = Path.Combine(targetFolder, $"{hash}.mp4.partial");
                var existingEntry = await _stateStore.GetAsync(hash, ct).ConfigureAwait(false);
                var localPath = existingEntry?.LocalPath ?? Path.Combine(targetFolder, $"{hash}.mp4");

                _logger.LogError(
                    "precache:enospc_during_download {Hash} {BytesDownloaded}",
                    hash, existingEntry?.DownloadedBytes ?? 0);

                // Clean up partial and mp4 files
                CleanupFile(partialPath);
                CleanupFile(localPath);

                await _stateStore.UpdateAsync(hash, e => new PrecacheEntry
                {
                    State = PrecacheState.Failed,
                    DownloadedBytes = 0,
                    SizeBytes = e?.SizeBytes ?? sizeBytes,
                    LastError = "disk_full_during_download",
                }, ct).ConfigureAwait(false);

                await ReportStatusAsync(hash, "failed", "disk_full_during_download", null, null, ct).ConfigureAwait(false);
                return;
            }

            // (8) SHA256 verify: hash IS the expected SHA256
            if (!string.Equals(downloadResult.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "precache:sha_mismatch {Hash} expected={Expected} actual={Actual}",
                    hash, hash, downloadResult.Sha256);

                // Delete .mp4 on mismatch
                var mismatchPath = downloadResult.FinalPath;
                if (!string.IsNullOrEmpty(mismatchPath) && File.Exists(mismatchPath))
                {
                    File.Delete(mismatchPath);
                    _logger.LogInformation("precache:deleted_mismatch {Path}", mismatchPath);
                }

                await _stateStore.UpdateAsync(hash, _ => new PrecacheEntry
                {
                    State = PrecacheState.Failed,
                    DownloadedBytes = 0,
                    SizeBytes = sizeBytes,
                    LastError = "sha_mismatch",
                }, ct).ConfigureAwait(false);

                await ReportStatusAsync(hash, "failed", "sha_mismatch", null, null, ct).ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("precache:sha_verify {Hash} ok=true", hash);

            // (9) State → done
            await _stateStore.UpdateAsync(hash, e => new PrecacheEntry
            {
                State = PrecacheState.Done,
                DownloadedBytes = sizeBytes,
                SizeBytes = sizeBytes,
                Sha256 = downloadResult.Sha256,
                LocalPath = downloadResult.FinalPath,
            }, ct).ConfigureAwait(false);

            _logger.LogInformation("precache:state_persisted {Entries} {Hash} state=done", 1, hash);

            // (10) Trigger library refresh
            var refreshStart = DateTimeOffset.UtcNow;
            TriggerLibraryRefresh();
            var refreshMs = (long)(DateTimeOffset.UtcNow - refreshStart).TotalMilliseconds;

            _logger.LogInformation("precache:library_refresh {Hash} {DurationMs}", hash, refreshMs);

            // (11) ReportStatus(done, sizeBytes)
            await ReportStatusAsync(hash, "done", null, sizeBytes, sizeBytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "precache:download_error {Hash} {ExType}", hash, ex.GetType().Name);

            try
            {
                await _stateStore.UpdateAsync(hash, e => new PrecacheEntry
                {
                    State = PrecacheState.Failed,
                    DownloadedBytes = e?.DownloadedBytes ?? 0,
                    SizeBytes = e?.SizeBytes ?? 0,
                    LastError = ex.GetType().Name,
                    LocalPath = e?.LocalPath,
                }, ct).ConfigureAwait(false);

                await ReportStatusAsync(hash, "failed", ex.GetType().Name, null, null, ct).ConfigureAwait(false);
            }
            catch (Exception reportEx)
            {
                _logger.LogWarning(reportEx, "precache:status_report_failed {Hash}", hash);
            }
        }
    }

    /// <summary>
    /// Verarbeitet die Release-Queue: Items mit state=release_requested.
    /// Loescht lokale .mp4-Dateien, entfernt StateStore-Eintraege,
    /// reportet state=released an die API.
    /// </summary>
    private async Task ProcessReleaseQueueAsync(CancellationToken ct)
    {
        var releaseQueue = await GetReleaseQueueAsync(ct).ConfigureAwait(false);

        if (releaseQueue.Count == 0)
        {
            return;
        }

        _logger.LogDebug("precache:release_queue {Count}", releaseQueue.Count);

        foreach (var item in releaseQueue)
        {
            await ProcessReleaseItemAsync(item, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verarbeitet ein einzelnes Release-Item: .mp4 loeschen, StateStore entfernen,
    /// released an API reporten.
    /// </summary>
    private async Task ProcessReleaseItemAsync(ReleaseQueueItem item, CancellationToken ct)
    {
        var hash = item.Hash;

        try
        {
            // (1) Look up local state to find the .mp4 path
            var entry = await _stateStore.GetAsync(hash, ct).ConfigureAwait(false);

            if (entry is not null && !string.IsNullOrEmpty(entry.LocalPath))
            {
                // (2) Delete the .mp4 file (best-effort)
                CleanupFile(entry.LocalPath);

                // Also try .partial file cleanup (in case download was interrupted)
                var partialPath = entry.LocalPath + ".partial";
                CleanupFile(partialPath);
            }

            // (3) Remove from local state store
            await _stateStore.RemoveAsync(hash, ct).ConfigureAwait(false);

            // (4) Report released to API
            await ReportStatusAsync(hash, "released", null, null, null, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "precache:manual_released {Hash} {ByUserId}",
                hash, item.UserId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "precache:release_failed {Hash}", hash);
            // Don't rethrow — continue processing other items
        }
    }

    #region Virtual methods — overridden in tests with fakes

    /// <summary>Pollt die Pre-Cache-Queue von der API.</summary>
    protected virtual async Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken ct)
    {
        var (apiUrl, apiToken) = GetApiConfig();
        var http = _httpClientFactory!.CreateClient("PrecacheApi");
        var client = new PrecacheApiClient(http, apiUrl, apiToken);
        return await client.GetQueueAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Pollt die Release-Queue (state=release_requested) von der API.</summary>
    protected virtual async Task<IReadOnlyList<ReleaseQueueItem>> GetReleaseQueueAsync(CancellationToken ct)
    {
        var (apiUrl, apiToken) = GetApiConfig();
        var http = _httpClientFactory!.CreateClient("PrecacheApi");
        var client = new PrecacheApiClient(http, apiUrl, apiToken);
        return await client.GetReleaseQueueAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reportet den Status eines Pre-Cache-Items an die API.</summary>
    protected virtual async Task ReportStatusAsync(
        string hash, string state, string? reason,
        long? bytesDownloaded, long? sizeBytes, CancellationToken ct)
    {
        var (apiUrl, apiToken) = GetApiConfig();
        var http = _httpClientFactory!.CreateClient("PrecacheApi");
        var client = new PrecacheApiClient(http, apiUrl, apiToken);
        await client.ReportStatusAsync(hash, state, reason, bytesDownloaded, sizeBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a library item by hash (for metadata: Title, Year, TmdbId, FileSize).</summary>
    protected virtual async Task<LibraryItem?> ResolveLibraryItemAsync(string hash, CancellationToken ct)
    {
        var (apiUrl, apiToken) = GetApiConfig();
        var http = _httpClientFactory!.CreateClient("PrecacheApi");
        var client = new OpenMediaApiClient(http, apiUrl, apiToken);
        var items = await client.GetLibraryAsync(ct).ConfigureAwait(false);
        return items.FirstOrDefault(i => string.Equals(i.Hash, hash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Download via PrecacheDownloader with Range-Resume, SHA256, atomic rename.</summary>
    protected virtual async Task<DownloadResult> PerformDownloadAsync(
        string hash, string signedUrl, long expectedSize,
        string targetFolder, IProgress<long>? progress, CancellationToken ct)
    {
        return await _downloader!.DownloadAsync(hash, signedUrl, expectedSize, targetFolder, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Signiert die Media-URL via MediaUrlSigner (S02).</summary>
    protected virtual string CreateSignedUrl(string hash, string userId)
    {
        var config = GetConfig();
        return MediaUrlSigner.SignMediaUrl(config.MediaSigningSecret, config.ApiUrl, hash, userId, ttlSeconds: 21600);
    }

    /// <summary>Triggert einen Jellyfin Library-Scan.</summary>
    protected virtual void TriggerLibraryRefresh()
    {
        _libraryManager!.QueueLibraryScan();
    }

    /// <summary>Liefert das STRM-Basisverzeichnis (gleiche Logik wie StrmSyncEngine).</summary>
    protected virtual string GetStrmDirectory()
    {
        var config = GetConfig();
        return GetEffectiveStrmDirectory(config.StrmDirectory, _applicationPaths!);
    }

    /// <summary>
    /// Prueft Disk-Quota vor Download. Virtual damit Tests mocken koennen.
    /// </summary>
    protected virtual (bool Ok, long FreeBytes, long RequiredBytes) CheckDiskQuota(
        string folder, long expectedSize)
    {
        return DiskQuotaChecker.CheckTargetFolder(folder, expectedSize);
    }

    #endregion

    #region Helpers

    private static PluginConfiguration GetConfig()
    {
        return Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration not available");
    }

    private static (string ApiUrl, string ApiToken) GetApiConfig()
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiUrl) || string.IsNullOrWhiteSpace(config.ApiToken))
        {
            throw new InvalidOperationException("ApiUrl or ApiToken not configured");
        }

        return (config.ApiUrl, config.ApiToken);
    }

    private static string GetEffectiveStrmDirectory(string? configured, IApplicationPaths paths)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(paths.DataPath, "openmedia-strm");
    }

    private static long ParseFileSize(string? fileSize)
    {
        if (string.IsNullOrWhiteSpace(fileSize))
        {
            return 0;
        }

        return long.TryParse(fileSize, out var size) ? size : 0;
    }

    /// <summary>
    /// Erkennt Disk-Full-Exceptions: IOException mit HResult ERROR_DISK_FULL (-2147024784 / 0x80070027)
    /// oder Message enthaelt 'ENOSPC' oder 'disk full'.
    /// </summary>
    internal static bool IsDiskFullException(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            // ERROR_DISK_FULL = 0x80070027 = -2147024784
            const int HResultDiskFull = unchecked((int)0x80070027);
            if (ioEx.HResult == HResultDiskFull)
            {
                return true;
            }

            if (ioEx.Message.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase) ||
                ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Also check inner exception
        if (ex.InnerException is not null)
        {
            return IsDiskFullException(ex.InnerException);
        }

        return false;
    }

    /// <summary>
    /// Best-effort Datei-Loeschung. Loggt Fehler aber wirft nicht.
    /// </summary>
    private void CleanupFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("precache:cleanup_file {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "precache:cleanup_failed {Path}", path);
        }
    }

    #endregion
}
