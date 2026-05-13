using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Scheduled-Task der die openmedia-API-Library mit dem konfigurierten STRM-Verzeichnis
/// synchronisiert. Schreibt {hash}.strm-Files, loescht eigene STRMs deren Hash nicht
/// mehr in der API steht, triggert anschliessend einen Jellyfin-Library-Scan.
/// </summary>
public class LibrarySyncTask : IScheduledTask
{
    private readonly ILogger<LibrarySyncTask> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;

    public LibrarySyncTask(
        ILogger<LibrarySyncTask> logger,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
    }

    public string Name => "openmedia: STRM-Library synchronisieren";

    public string Key => "OpenMediaLibrarySync";

    public string Description =>
        "Synchronisiert deine openmedia-Library als STRM-Dateien in das konfigurierte Verzeichnis.";

    public string Category => "openmedia";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(15).Ticks,
            },
        };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogWarning("Plugin-Configuration nicht verfuegbar — Sync uebersprungen.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ApiUrl) || string.IsNullOrWhiteSpace(config.ApiToken))
        {
            _logger.LogWarning("ApiUrl oder ApiToken nicht gesetzt — Sync uebersprungen.");
            return;
        }

        var strmDir = GetEffectiveStrmDirectory(config.StrmDirectory, _applicationPaths);

        progress.Report(5);

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
            return;
        }

        progress.Report(40);

        StrmSyncResult result;
        try
        {
            result = StrmSyncEngine.Sync(strmDir, items, config.ApiUrl, config.ApiToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STRM-Sync fehlgeschlagen.");
            return;
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

        progress.Report(80);

        if (result.Added > 0 || result.Updated > 0 || result.Removed > 0)
        {
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("Jellyfin Library-Scan eingereiht (STRM-Aenderungen erkannt).");
        }

        progress.Report(100);
    }

    /// <summary>
    /// Resolved den effektiven STRM-Pfad: nicht-leerer Config-Wert → genau dieser,
    /// sonst Default unter ApplicationPaths.DataPath.
    /// </summary>
    internal static string GetEffectiveStrmDirectory(string? configured, IApplicationPaths paths)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine(paths.DataPath, "openmedia-strm");
    }
}
