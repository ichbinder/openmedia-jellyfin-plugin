using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Täglicher Scheduled-Task der abgelaufene Pre-Caches löscht.
/// Ein Pre-Cache gilt als abgelaufen wenn:
///   state == 'done' UND (now - lastEventAt) > ttlSeconds
/// Nach der Löschung wird .strm über den nächsten Sync-Tick wiederhergestellt.
/// </summary>
public class PrecacheTtlCleanupService : IScheduledTask
{
    /// <summary>Default TTL: 7 Tage in Sekunden.</summary>
    internal const int DefaultTtlSeconds = 7 * 24 * 60 * 60;

    private readonly ILogger<PrecacheTtlCleanupService> _logger;
    private readonly PrecacheStateStore _stateStore;

    public PrecacheTtlCleanupService(
        ILogger<PrecacheTtlCleanupService> logger,
        PrecacheStateStore stateStore)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public string Name => "openmedia: Abgelaufene Pre-Caches löschen";

    public string Key => "OpenMediaPrecacheTtlCleanup";

    public string Description =>
        "Löscht abgelaufene Pre-Cache-Dateien (.mp4) deren TTL überschritten ist. " +
        "Die .strm-Dateien werden beim nächsten Sync automatisch wiederhergestellt.";

    public string Category => "openmedia";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks, // 3 AM
            },
        };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("precache:ttl_cleanup_started");

        var entries = await _stateStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var doneEntries = entries
            .Where(kv => kv.Value.State == PrecacheState.Done)
            .ToList();

        if (doneEntries.Count == 0)
        {
            _logger.LogInformation("precache:ttl_cleanup_nothing_to_do");
            progress.Report(100);
            return;
        }

        var evicted = 0;
        var errors = 0;

        for (var i = 0; i < doneEntries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (hash, entry) = doneEntries[i];
            var ttlSeconds = entry.TtlSeconds ?? DefaultTtlSeconds;
            var age = DateTime.UtcNow - entry.LastEventAt;

            if (age.TotalSeconds <= ttlSeconds)
            {
                continue; // Not expired yet
            }

            var ageDays = age.TotalDays;

            // Delete the cached file
            var deleteFailed = false;
            if (!string.IsNullOrEmpty(entry.LocalPath))
            {
                try
                {
                    if (File.Exists(entry.LocalPath))
                    {
                        File.Delete(entry.LocalPath);
                    }
                    // File doesn't exist — that's fine (already deleted or never created)
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "precache:ttl_delete_failed {Hash} {Path}", hash, entry.LocalPath);
                    deleteFailed = true;
                    // Continue with next item — don't crash
                }
            }

            if (deleteFailed)
            {
                errors++;
                continue;
            }

            // Remove from state store
            try
            {
                await _stateStore.RemoveAsync(hash, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "precache:ttl_remove_failed {Hash}", hash);
                errors++;
                continue;
            }

            _logger.LogInformation(
                "precache:ttl_evicted {Hash} {AgeDays:F1}",
                hash, ageDays);

            evicted++;

            // Report progress
            var pct = (double)(i + 1) / doneEntries.Count * 100;
            progress.Report(pct);
        }

        _logger.LogInformation(
            "precache:ttl_cleanup_done {Evicted} {Errors} {Total}",
            evicted, errors, doneEntries.Count);

        progress.Report(100);
    }
}
