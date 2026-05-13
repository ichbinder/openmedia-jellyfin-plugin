using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OpenMedia.Api;

/// <summary>
/// Eine Library-Position (ein Film) aus der openmedia-API.
/// Mapped auf GET /jellyfin/library Response-Items.
/// </summary>
public sealed record LibraryItem(
    string Hash,
    long? TmdbId,
    string? Title,
    int? Year,
    string? FileSize,
    int? Duration,
    string? Resolution);

/// <summary>
/// Minimaler HTTP-Client gegen die openmedia-API.
/// Bearer-Auth mit dem ApiToken aus PluginConfiguration.
/// </summary>
public class OpenMediaApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    public OpenMediaApiClient(HttpClient http, string baseUrl, string token)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        _token = token ?? string.Empty;
    }

    /// <summary>
    /// Holt alle Library-Eintraege des authentifizierten Users.
    /// </summary>
    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/jellyfin/library");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        var payload = await res.Content.ReadFromJsonAsync<LibraryResponse>(JsonOptions, ct).ConfigureAwait(false);
        return payload?.Items ?? Array.Empty<LibraryItem>();
    }

    private sealed record LibraryResponse(IReadOnlyList<LibraryItem> Items);
}
