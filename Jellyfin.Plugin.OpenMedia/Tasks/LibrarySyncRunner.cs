using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Fuehrt einen kompletten STRM-Sync-Lauf aus. Identische Logik egal ob der Trigger
/// der 15-Min-ScheduledTask ist oder das 15s-Polling.
/// </summary>
public interface ILibrarySyncRunner
{
    /// <summary>
    /// Ein Sync-Durchlauf. Returnt das Ergebnis fuer Logging/Tests; bei einem internen
    /// Fehler (z.B. API-Fetch fehlgeschlagen) wird das im Log vermerkt und null
    /// zurueckgegeben — der Caller soll trotzdem weiterleben.
    /// </summary>
    Task<StrmSyncResult?> RunOnceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Default-Implementierung: liest Plugin-Configuration, holt Library, ruft StrmSyncEngine,
/// triggert Jellyfin-Library-Scan bei Aenderungen.
/// </summary>
public class LibrarySyncRunner : ILibrarySyncRunner
{
    private readonly ILogger<LibrarySyncRunner> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly PrecacheStateStore _precacheStateStore;

    public LibrarySyncRunner(
        ILogger<LibrarySyncRunner> logger,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        PrecacheStateStore precacheStateStore)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _precacheStateStore = precacheStateStore;
    }

    public async Task<StrmSyncResult?> RunOnceAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogWarning("Plugin-Configuration nicht verfuegbar — Sync uebersprungen.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.ApiUrl) || string.IsNullOrWhiteSpace(config.ApiToken))
        {
            _logger.LogWarning("ApiUrl oder ApiToken nicht gesetzt — Sync uebersprungen.");
            return null;
        }

        var strmDir = LibrarySyncTask.GetEffectiveStrmDirectory(config.StrmDirectory, _applicationPaths);

        var tvDir = LibrarySyncTask.GetEffectiveTvStrmDirectory(config.TvStrmDirectory, _applicationPaths);

        var http = _httpClientFactory.CreateClient(nameof(OpenMediaApiClient));
        var client = new OpenMediaApiClient(http, config.ApiUrl, config.ApiToken);

        IReadOnlyList<LibraryItem> items;
        try
        {
            items = await client.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library-Fetch fehlgeschlagen — Sync abgebrochen.");
            return null;
        }

        StrmSyncResult result;
        try
        {
            // Pre-Cache-aware sync: loescht .strm fuer gecachte Hashes,
            // skippt .strm-Schreiben wenn PrecacheWorker bereits .mp4 abgelegt hat.
            var precacheState = _precacheStateStore;
            var movies = items.Where(i => i.Kind != "episode").ToList();
            var episodes = items.Where(i => i.Kind == "episode").ToList();
            if (precacheState is not null)
            {
                result = await StrmSyncEngine.SyncAsync(
                    strmDir, movies, config.ApiUrl, config.ApiToken,
                    precacheState, _logger, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = StrmSyncEngine.Sync(strmDir, movies, config.ApiUrl, config.ApiToken);
            }

            var episodeResult = await EpisodeStrmSyncEngine.SyncAsync(
                tvDir, episodes, config.ApiUrl, config.ApiToken,
                precacheState, _logger, cancellationToken).ConfigureAwait(false);
            result = StrmSyncResult.Combine(result, episodeResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STRM-Sync fehlgeschlagen.");
            return null;
        }

        _logger.LogInformation(
            "openmedia STRM-Sync abgeschlossen: dir={Dir} added={Added} updated={Updated} unchanged={Unchanged} removed={Removed} skipped={Skipped} foreign={Foreign} rejected={Rejected}",
            strmDir,
            result.Added,
            result.Updated,
            result.Unchanged,
            result.Removed,
            result.Skipped,
            result.Foreign,
            result.Rejected);

        if (result.Added > 0 || result.Updated > 0 || result.Removed > 0)
        {
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("Jellyfin Library-Scan eingereiht (STRM-Aenderungen erkannt).");
        }

        return result;
    }
}
