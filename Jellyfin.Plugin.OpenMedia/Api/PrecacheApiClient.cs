using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OpenMedia.Api;

/// <summary>
/// Ein Eintrag aus der Pre-Cache-Queue der API.
/// </summary>
public sealed record QueueItem(
    string Hash,
    string UserId,
    DateTime RequestedAt);

/// <summary>
/// Ein Eintrag aus der Release-Queue der API (state=release_requested).
/// </summary>
public sealed record ReleaseQueueItem(
    string Hash,
    string UserId,
    DateTime LastEventAt);

/// <summary>
/// HTTP-Client fuer die Pre-Cache-Endpoints der openmedia-API.
/// Pollt /jellyfin/precache/queue und reportet Status via /:hash/status.
/// </summary>
public class PrecacheApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    /// <summary>Max Retries bei 5xx/Network-Errors.</summary>
    private const int MaxRetries = 3;

    /// <summary>Timeout pro HTTP-Call.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public PrecacheApiClient(HttpClient http, string baseUrl, string token)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        _token = token ?? throw new ArgumentNullException(nameof(token));
    }

    /// <summary>
    /// GET /jellyfin/precache/queue — holt alle ausstehenden Pre-Cache-Anforderungen.
    /// Retries: 3x exponential backoff (1s, 2s, 4s) bei 5xx + Network-Errors;
    /// bei 4xx kein Retry, Exception. Timeout 30s pro Call.
    /// </summary>
    public async Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/jellyfin/precache/queue");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var res = await SendWithRetryAsync(req, ct).ConfigureAwait(false);

        var payload = await res.Content.ReadFromJsonAsync<QueueResponse>(JsonOptions, ct).ConfigureAwait(false);
        return payload?.Items ?? Array.Empty<QueueItem>();
    }

    /// <summary>
    /// GET /jellyfin/precache/release-queue — holt alle Items mit state=release_requested.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseQueueItem>> GetReleaseQueueAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/jellyfin/precache/release-queue");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var res = await SendWithRetryAsync(req, ct).ConfigureAwait(false);

        var payload = await res.Content.ReadFromJsonAsync<ReleaseQueueResponse>(JsonOptions, ct).ConfigureAwait(false);
        return payload?.Items ?? Array.Empty<ReleaseQueueItem>();
    }

    /// <summary>
    /// POST /jellyfin/precache/:hash/status — meldet den aktuellen Download-Status.
    /// </summary>
    public async Task ReportStatusAsync(
        string hash,
        string state,
        string? reason = null,
        long? bytesDownloaded = null,
        long? sizeBytes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var body = new StatusPayload
        {
            State = state,
            Reason = reason,
            BytesDownloaded = bytesDownloaded,
            SizeBytes = sizeBytes,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/jellyfin/precache/{hash}/status")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        await SendWithRetryAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sendet einen HttpRequest mit Retry-Logik: 3x exponential backoff bei 5xx/Network;
    /// 4xx wird sofort geworfen. Cancellation propagiert.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var cloned = await CloneRequestAsync(request).ConfigureAwait(false);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(CallTimeout);

                    var response = await _http.SendAsync(cloned, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    var statusCode = (int)response.StatusCode;

                    // 4xx → no retry, throw immediately
                    if (statusCode >= 400 && statusCode < 500)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        throw new ApiClientException(
                            $"API returned {(HttpStatusCode)statusCode} for {request.RequestUri}: {responseBody}",
                            statusCode);
                    }

                    // 5xx → retry with exponential backoff
                    lastException = new HttpRequestException(
                        $"API returned {(HttpStatusCode)statusCode} for {request.RequestUri}");
                }
                catch (ApiClientException)
                {
                    throw; // 4xx — don't retry, propagate immediately
                }
            }
            catch (ApiClientException)
            {
                throw; // 4xx — don't retry, propagate immediately
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout (not user cancellation) → retry
                lastException = new TimeoutException(
                    $"HTTP call to {request.RequestUri} timed out after {CallTimeout.TotalSeconds}s");
            }
            catch (HttpRequestException ex)
            {
                // Network error → retry
                lastException = ex;
            }

            // Exponential backoff: 1s, 2s — but not on last attempt
            if (attempt < MaxRetries - 1)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1, 2
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw lastException ?? new InvalidOperationException("Retry loop exited without exception");
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        clone.Version = original.Version;

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content is not null)
        {
            var contentBytes = await original.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(contentBytes);

            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private sealed record QueueResponse(IReadOnlyList<QueueItem> Items);
    private sealed record ReleaseQueueResponse(IReadOnlyList<ReleaseQueueItem> Items);

    private sealed class StatusPayload
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("bytesDownloaded")]
        public long? BytesDownloaded { get; set; }

        [JsonPropertyName("sizeBytes")]
        public long? SizeBytes { get; set; }
    }
}

/// <summary>
/// Thrown when the API returns a 4xx status code. No retry.
/// </summary>
public sealed class ApiClientException : HttpRequestException
{
    public new int StatusCode { get; }

    public ApiClientException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
