using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia;

/// <summary>
/// Stellt beim Jellyfin-Start sicher dass eine Movies-Library mit dem Namen
/// <see cref="Configuration.PluginConfiguration.LibraryName"/> existiert die auf das
/// konfigurierte STRM-Verzeichnis zeigt. Damit muss der User die Library nicht manuell anlegen.
/// </summary>
public class LibraryAutoCreateService : IHostedService
{
    private readonly ILogger<LibraryAutoCreateService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;

    public LibraryAutoCreateService(
        ILogger<LibraryAutoCreateService> logger,
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureLibraryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Auto-Create darf den Jellyfin-Start nie blockieren.
            _logger.LogError(ex, "openmedia Library Auto-Create fehlgeschlagen.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task EnsureLibraryAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            _logger.LogDebug("Plugin-Configuration nicht verfuegbar — Auto-Create uebersprungen.");
            return;
        }

        if (!config.AutoCreateLibrary)
        {
            _logger.LogDebug("AutoCreateLibrary=false — Auto-Create uebersprungen.");
            return;
        }

        var libraryName = (config.LibraryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            _logger.LogWarning("LibraryName ist leer — Auto-Create uebersprungen.");
            return;
        }

        var strmDir = LibrarySyncTask.GetEffectiveStrmDirectory(config.StrmDirectory, _applicationPaths);

        // Verzeichnis sicherstellen, damit der erste Library-Scan einen Pfad findet.
        try
        {
            Directory.CreateDirectory(strmDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Konnte STRM-Verzeichnis nicht anlegen ({Dir}) — Auto-Create abgebrochen.",
                strmDir);
            return;
        }

        var existing = _libraryManager.GetVirtualFolders();

        // Existiert bereits eine Library mit diesem Namen → nichts tun.
        var byName = existing.FirstOrDefault(
            f => string.Equals(f.Name, libraryName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            var pathsMatch = byName.Locations is not null
                && byName.Locations.Any(p => PathsEqual(p, strmDir));
            if (!pathsMatch)
            {
                _logger.LogWarning(
                    "Library {Name} existiert bereits, zeigt aber NICHT auf {Dir}. "
                        + "Belasse die Library wie sie ist — bitte manuell pruefen.",
                    libraryName,
                    strmDir);
            }
            else
            {
                _logger.LogDebug("Library {Name} existiert bereits — nichts zu tun.", libraryName);
            }

            return;
        }

        // Falls eine andere Library schon auf denselben Pfad zeigt, doppelt anlegen vermeiden.
        var byPath = existing.FirstOrDefault(
            f => f.Locations is not null && f.Locations.Any(p => PathsEqual(p, strmDir)));
        if (byPath is not null)
        {
            _logger.LogWarning(
                "Library {Existing} zeigt bereits auf {Dir} — keine zweite Library {Name} angelegt.",
                byPath.Name,
                strmDir,
                libraryName);
            return;
        }

        var options = new LibraryOptions
        {
            PathInfos = new[]
            {
                new MediaPathInfo { Path = strmDir },
            },
        };

        _logger.LogInformation(
            "Lege Jellyfin-Library {Name} (movies) mit Pfad {Dir} an.",
            libraryName,
            strmDir);

        await _libraryManager
            .AddVirtualFolder(libraryName, CollectionTypeOptions.movies, options, refreshLibrary: true)
            .ConfigureAwait(false);

        _logger.LogInformation("Library {Name} erfolgreich angelegt.", libraryName);
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var na = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var nb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}
