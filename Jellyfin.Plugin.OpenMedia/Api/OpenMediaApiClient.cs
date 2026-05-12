using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenMedia.Api;

/// <summary>
/// HTTP-Client für die openmedia-API.
/// Liest Base-URL und Bearer-Token aus <see cref="Plugin.Instance"/>.Configuration.
/// </summary>
public class OpenMediaApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger<OpenMediaApiClient> _logger;

    public OpenMediaApiClient(HttpClient httpClient, ILogger<OpenMediaApiClient> logger)
    {
        _http = httpClient;
        _logger = logger;
    }

    /// <summary>Holt die UserLibrary des konfigurierten Tokens. Throws OpenMediaApiException bei Fehlern.</summary>
    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken cancellationToken)
    {
        var (baseUrl, token) = ReadConfig();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/jellyfin/library");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[openmedia] GET /jellyfin/library network error");
            throw new OpenMediaApiException("openmedia API unreachable", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "[openmedia] GET /jellyfin/library status={Status}",
                    (int)response.StatusCode);
                throw new OpenMediaApiException(
                    $"openmedia /jellyfin/library returned {(int)response.StatusCode}",
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            LibraryResponse? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<LibraryResponse>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[openmedia] GET /jellyfin/library JSON parse error");
                throw new OpenMediaApiException("openmedia /jellyfin/library returned invalid JSON", ex);
            }

            var items = payload?.Items ?? Array.Empty<LibraryItem>();
            _logger.LogInformation(
                "[openmedia] GET /jellyfin/library status=200 count={Count}",
                items.Length);
            return items;
        }
    }

    /// <summary>
    /// Resolved den finalen S3-URL für einen Hash. Die API antwortet mit HTTP 302 — der Location-Header ist die frische
    /// Presigned-URL. Throws OpenMediaApiException bei 404/410/sonstigen Fehlern.
    /// </summary>
    public async Task<string> GetStreamUrlAsync(string hash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException("hash must not be empty", nameof(hash));
        }

        var (baseUrl, token) = ReadConfig();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/jellyfin/stream/{hash}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            // Achtung: Der HttpClient muss mit AllowAutoRedirect=false konfiguriert sein (siehe ServiceRegistrator).
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[openmedia] GET /jellyfin/stream/{Hash} network error", hash);
            throw new OpenMediaApiException("openmedia API unreachable", ex);
        }

        using (response)
        {
            // Erwartet: 302 Found (oder 307/301) mit Location-Header zur S3-Presigned-URL.
            if (response.StatusCode is HttpStatusCode.Found
                or HttpStatusCode.Redirect
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.MovedPermanently
                or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location?.ToString();
                if (string.IsNullOrWhiteSpace(location))
                {
                    _logger.LogError("[openmedia] GET /jellyfin/stream/{Hash} redirect without Location", hash);
                    throw new OpenMediaApiException("openmedia /jellyfin/stream redirect without Location");
                }

                _logger.LogInformation(
                    "[openmedia] GET /jellyfin/stream/{Hash} status={Status} → S3 URL",
                    hash,
                    (int)response.StatusCode);
                return location;
            }

            _logger.LogError(
                "[openmedia] GET /jellyfin/stream/{Hash} status={Status}",
                hash,
                (int)response.StatusCode);
            throw new OpenMediaApiException(
                $"openmedia /jellyfin/stream/{hash} returned {(int)response.StatusCode}",
                response.StatusCode);
        }
    }

    private static (string BaseUrl, string Token) ReadConfig()
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new OpenMediaApiException("Plugin not initialised");

        if (string.IsNullOrWhiteSpace(config.ApiUrl))
        {
            throw new OpenMediaApiException("openmedia API URL not configured");
        }

        if (string.IsNullOrWhiteSpace(config.ApiToken))
        {
            throw new OpenMediaApiException("openmedia API token not configured");
        }

        return (config.ApiUrl.TrimEnd('/'), config.ApiToken);
    }
}
