using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Api.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Post-Scan-Task: synct die UserLibrary aus der openmedia-API in eine Jellyfin-CollectionFolder mit Namen "openmedia".
/// Lookup: ProviderIds["OpenMediaHash"]. Neue Items werden via ILibraryManager.CreateItem angelegt und via
/// RefreshMetadata an Jellyfins TMDB-Provider durchgereicht (für Poster + Plot).
/// </summary>
public class LibrarySyncTask : ILibraryPostScanTask
{
    private const string OpenMediaLibraryName = "openmedia";
    private const string OpenMediaHashProviderKey = "OpenMediaHash";

    private readonly OpenMediaApiClient _apiClient;
    private readonly ILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<LibrarySyncTask> _logger;

    public LibrarySyncTask(
        OpenMediaApiClient apiClient,
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        ILogger<LibrarySyncTask> logger)
    {
        _apiClient = apiClient;
        _libraryManager = libraryManager;
        _directoryService = directoryService;
        _logger = logger;
    }

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[openmedia] sync started");

        var parent = FindOpenMediaCollectionFolder();
        if (parent is null)
        {
            _logger.LogWarning(
                "[openmedia] no Jellyfin library named '{Name}' found — create one in Admin → Libraries first",
                OpenMediaLibraryName);
            progress.Report(100);
            return;
        }

        IReadOnlyList<LibraryItem> remoteItems;
        try
        {
            remoteItems = await _apiClient.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OpenMediaApiException ex)
        {
            _logger.LogError(ex, "[openmedia] sync aborted — cannot fetch remote library");
            progress.Report(100);
            return;
        }

        var existingByHash = LoadExistingMoviesByHash(parent);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);

        var refreshOptions = new MetadataRefreshOptions(_directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            EnableRemoteContentProbe = false,
        };

        int total = remoteItems.Count;
        int added = 0;
        int updated = 0;
        int skipped = 0;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = remoteItems[i];

            if (string.IsNullOrWhiteSpace(item.Hash) || item.TmdbId <= 0)
            {
                skipped++;
                continue;
            }

            seenHashes.Add(item.Hash);

            if (existingByHash.TryGetValue(item.Hash, out var existing))
            {
                if (await UpdateExistingMovie(existing, item, cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }
            else
            {
                var movie = BuildMovie(item, parent);
                _libraryManager.CreateItem(movie, parent);
                await movie.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
                added++;
            }

            if (total > 0)
            {
                progress.Report(((double)(i + 1) / total) * 100.0);
            }
        }

        int removed = 0;
        foreach (var kvp in existingByHash)
        {
            if (!seenHashes.Contains(kvp.Key))
            {
                _libraryManager.DeleteItem(
                    kvp.Value,
                    new DeleteOptions { DeleteFileLocation = false },
                    notifyParentItem: true);
                removed++;
            }
        }

        _logger.LogInformation(
            "[openmedia] sync done added={Added} updated={Updated} removed={Removed} skipped={Skipped}",
            added,
            updated,
            removed,
            skipped);

        progress.Report(100);
    }

    private Folder? FindOpenMediaCollectionFolder()
    {
        var folders = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.CollectionFolder },
            Name = OpenMediaLibraryName,
        });

        return folders.OfType<Folder>().FirstOrDefault();
    }

    private Dictionary<string, Movie> LoadExistingMoviesByHash(Folder parent)
    {
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            ParentId = parent.Id,
            Recursive = true,
        }).OfType<Movie>();

        var dict = new Dictionary<string, Movie>(StringComparer.Ordinal);
        foreach (var movie in movies)
        {
            var hash = movie.GetProviderId(OpenMediaHashProviderKey);
            if (!string.IsNullOrWhiteSpace(hash))
            {
                dict[hash] = movie;
            }
        }

        return dict;
    }

    private Movie BuildMovie(LibraryItem item, Folder parent)
    {
        var movie = new Movie
        {
            Id = _libraryManager.GetNewItemId(
                $"openmedia:{item.Hash}",
                typeof(Movie)),
            Name = item.Title,
            OriginalTitle = item.Title,
            ProductionYear = item.Year,
            ParentId = parent.Id,
            Path = $"openmedia://{item.Hash}",
            IsVirtualItem = false,
            Container = "mp4",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        movie.SetProviderId(MetadataProvider.Tmdb, item.TmdbId.ToString(CultureInfo.InvariantCulture));
        movie.SetProviderId(OpenMediaHashProviderKey, item.Hash);

        if (item.Duration is int durationSeconds && durationSeconds > 0)
        {
            movie.RunTimeTicks = durationSeconds * TimeSpan.TicksPerSecond;
        }

        if (long.TryParse(item.FileSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0)
        {
            movie.Size = size;
        }

        return movie;
    }

    private async Task<bool> UpdateExistingMovie(Movie movie, LibraryItem item, CancellationToken cancellationToken)
    {
        bool changed = false;

        if (movie.ProductionYear != item.Year && item.Year is int year)
        {
            movie.ProductionYear = year;
            changed = true;
        }

        if (!string.Equals(movie.Name, item.Title, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Title))
        {
            movie.Name = item.Title;
            changed = true;
        }

        var existingTmdb = movie.GetProviderId(MetadataProvider.Tmdb);
        var newTmdb = item.TmdbId.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(existingTmdb, newTmdb, StringComparison.Ordinal))
        {
            movie.SetProviderId(MetadataProvider.Tmdb, newTmdb);
            changed = true;
        }

        if (changed)
        {
            await _libraryManager.UpdateItemAsync(
                movie,
                movie.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }
}
