using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.MediaSources;

/// <summary>
/// Liefert beim Play für openmedia-Items eine frische S3-Presigned-URL.
/// Items werden anhand der ProviderId "OpenMediaHash" erkannt (gesetzt vom LibrarySyncTask).
/// </summary>
public class OpenMediaMediaSourceProvider : IMediaSourceProvider
{
    private const string OpenMediaHashProviderKey = "OpenMediaHash";

    private readonly OpenMediaApiClient _apiClient;
    private readonly ILogger<OpenMediaMediaSourceProvider> _logger;

    public OpenMediaMediaSourceProvider(OpenMediaApiClient apiClient, ILogger<OpenMediaMediaSourceProvider> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var hash = item.GetProviderId(OpenMediaHashProviderKey);
        if (string.IsNullOrWhiteSpace(hash))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        string streamUrl;
        try
        {
            streamUrl = await _apiClient.GetStreamUrlAsync(hash, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenMediaApiException ex)
        {
            _logger.LogError(ex, "[openmedia] resolve failed hash={Hash}", hash);
            return Array.Empty<MediaSourceInfo>();
        }

        var container = InferContainerFromUrl(streamUrl);

        _logger.LogInformation(
            "[openmedia] resolve hash={Hash} container={Container}",
            hash,
            container);

        var source = new MediaSourceInfo
        {
            Id = hash,
            Name = "openmedia (S3)",
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            Container = container,
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            SupportsProbing = true,
            RunTimeTicks = item.RunTimeTicks,
            Size = item is Video video ? video.Size : null,
            EncoderProtocol = MediaProtocol.Http,
            // OpenToken wird pro Stream-Refresh neu generiert, damit Jellyfin URLs nicht ueber TTL hinweg cached.
            OpenToken = $"openmedia:{hash}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
        };

        return new[] { source };
    }

    public Task<ILiveStream> OpenMediaSource(
        string openToken,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        // openmedia liefert keine Live-Streams; wir nutzen ausschliesslich Direct-Play via Path-URL.
        throw new NotImplementedException("openmedia does not provide live streams.");
    }

    public string Name => "openmedia";

    private static string InferContainerFromUrl(string url)
    {
        // S3-Presigned-URLs enthalten meist die Datei-Endung im Pfad-Teil vor dem '?'.
        var path = url;
        var queryIdx = url.IndexOf('?', StringComparison.Ordinal);
        if (queryIdx > 0)
        {
            path = url[..queryIdx];
        }

        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0 || lastDot == path.Length - 1)
        {
            return "mp4";
        }

        var ext = path[(lastDot + 1)..].ToLowerInvariant();
        return ext switch
        {
            "mkv" => "mkv",
            "mp4" => "mp4",
            "m4v" => "mp4",
            "webm" => "webm",
            _ => "mp4",
        };
    }
}
