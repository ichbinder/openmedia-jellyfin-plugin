using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Scheduled-Task der die openmedia-API-Library mit dem konfigurierten STRM-Verzeichnis
/// synchronisiert. Faellt seit dem Polling-Service auf 15 Min Intervall zurueck und dient
/// als Safety-Net fuer Faelle in denen der Polling-Service nicht laeuft (Plugin-Update,
/// Server-Neustart in den 15s Polling-Pause hinein etc.).
/// </summary>
public class LibrarySyncTask : IScheduledTask
{
    private readonly ILogger<LibrarySyncTask> _logger;
    private readonly ILibrarySyncRunner _runner;

    public LibrarySyncTask(ILogger<LibrarySyncTask> logger, ILibrarySyncRunner runner)
    {
        _logger = logger;
        _runner = runner;
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
        progress.Report(5);
        await _runner.RunOnceAsync(cancellationToken).ConfigureAwait(false);
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
