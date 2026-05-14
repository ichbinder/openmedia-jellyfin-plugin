using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Tasks;

/// <summary>
/// Background-Service der alle <see cref="PollingInterval"/> GET /jellyfin/library/version
/// abruft. Aendert sich der ETag, wird der <see cref="ILibrarySyncRunner"/> getriggert —
/// also derselbe Codepfad wie der ScheduledTask, nur viel haeufiger.
/// </summary>
public class LibraryPollingService : BackgroundService
{
    /// <summary>Polling-Intervall. Bewusst klein, damit die Bibliothek sich "live" anfuehlt.</summary>
    internal static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(15);

    /// <summary>Backoff bei Fehlern, damit ein kaputter API-Endpoint nicht 4× pro Minute spammt.</summary>
    internal static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(1);

    private readonly ILogger<LibraryPollingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibrarySyncRunner _runner;

    private string? _lastEtag;

    public LibraryPollingService(
        ILogger<LibraryPollingService> logger,
        IHttpClientFactory httpClientFactory,
        ILibrarySyncRunner runner)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _runner = runner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "openmedia LibraryPollingService gestartet (Intervall {Interval}s).",
            (int)PollingInterval.TotalSeconds);

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

        _logger.LogInformation("openmedia LibraryPollingService gestoppt.");
    }

    /// <summary>
    /// Ein einzelner Polling-Tick. Returnt das Delay bis zum naechsten Tick (Normalfall oder Backoff).
    /// Internal damit Tests einen Tick einzeln treiben koennen.
    /// </summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null
            || string.IsNullOrWhiteSpace(config.ApiUrl)
            || string.IsNullOrWhiteSpace(config.ApiToken))
        {
            // Plugin noch nicht konfiguriert — leise warten und beim naechsten Tick neu pruefen.
            return PollingInterval;
        }

        try
        {
            var http = _httpClientFactory.CreateClient(nameof(OpenMediaApiClient));
            var client = new OpenMediaApiClient(http, config.ApiUrl, config.ApiToken);

            var version = await client.GetLibraryVersionAsync(ct).ConfigureAwait(false);

            if (string.Equals(_lastEtag, version.Etag, StringComparison.Ordinal))
            {
                return PollingInterval;
            }

            _logger.LogInformation(
                "Library-Version geaendert ({Old} → {New}, count={Count}) — Sync wird angestossen.",
                _lastEtag ?? "<initial>",
                version.Etag,
                version.Count);

            // Sync triggern. Erst danach den neuen ETag speichern damit wir bei Sync-Fehlern
            // beim naechsten Tick erneut versuchen.
            var result = await _runner.RunOnceAsync(ct).ConfigureAwait(false);
            if (result is not null)
            {
                _lastEtag = version.Etag;
            }

            return PollingInterval;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Library-Version Poll fehlgeschlagen — Backoff {Backoff}s.",
                (int)ErrorBackoff.TotalSeconds);
            return ErrorBackoff;
        }
    }
}
