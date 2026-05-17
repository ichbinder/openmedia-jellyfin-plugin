using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class PrecacheApiClientTests
{
    private const string BaseUrl = "https://api.test.com";
    private const string Token = "om_test_token_123";

    private static (PrecacheApiClient client, FakeHandler handler) CreateClient()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler);
        var client = new PrecacheApiClient(http, BaseUrl, Token);
        return (client, handler);
    }

    #region GetQueueAsync

    [Fact]
    public async Task GetQueueAsync_Parse_Success()
    {
        var (client, handler) = CreateClient();
        handler.Setup(req =>
        {
            Assert.Equal("/jellyfin/precache/queue", req.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            Assert.Equal(Token, req.Headers.Authorization.Parameter);

            var items = new[]
            {
                new { hash = "abc123", userId = "user1", requestedAt = "2025-01-01T00:00:00Z" },
                new { hash = "def456", userId = "user2", requestedAt = "2025-01-02T00:00:00Z" },
            };
            var json = JsonSerializer.Serialize(new { items }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var result = await client.GetQueueAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("abc123", result[0].Hash);
        Assert.Equal("user1", result[0].UserId);
        Assert.Equal("def456", result[1].Hash);
    }

    [Fact]
    public async Task GetQueueAsync_401_Throws_HttpRequestException()
    {
        var (client, handler) = CreateClient();
        var callCount = 0;
        handler.Setup(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Unauthorized"),
            };
        });

        var ex = await Assert.ThrowsAsync<ApiClientException>(
            () => client.GetQueueAsync(CancellationToken.None));

        Assert.Contains("Unauthorized", ex.Message);
        Assert.Equal(401, ex.StatusCode);
        // 4xx should NOT retry
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetQueueAsync_500_Retries_Then_Throws()
    {
        var (client, handler) = CreateClient();
        var callCount = 0;
        handler.Setup(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetQueueAsync(CancellationToken.None));

        Assert.Contains("InternalServerError", ex.Message);
        // Should retry 3 times total
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task GetQueueAsync_Empty_Response()
    {
        var (client, handler) = CreateClient();
        handler.Setup(_ =>
        {
            var json = JsonSerializer.Serialize(new { items = Array.Empty<object>() },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var result = await client.GetQueueAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    #endregion

    #region ReportStatusAsync

    [Fact]
    public async Task ReportStatusAsync_Sends_Correct_Body()
    {
        var (client, handler) = CreateClient();
        StatusPayloadCapture? captured = null;

        handler.Setup(async req =>
        {
            Assert.Equal("/jellyfin/precache/abc123/status", req.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, req.Method);

            var body = await req.Content!.ReadAsStringAsync();
            captured = JsonSerializer.Deserialize<StatusPayloadCapture>(body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await client.ReportStatusAsync(
            "abc123",
            "downloading",
            reason: null,
            bytesDownloaded: 1024,
            sizeBytes: 5000,
            ct: CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("downloading", captured!.State);
        Assert.Null(captured.Reason);
        Assert.Equal(1024, captured.BytesDownloaded);
        Assert.Equal(5000, captured.SizeBytes);
    }

    [Fact]
    public async Task ReportStatusAsync_With_Reason()
    {
        var (client, handler) = CreateClient();
        handler.Setup(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Should not throw
        await client.ReportStatusAsync(
            "hash456",
            "failed",
            reason: "disk full",
            ct: CancellationToken.None);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (client, handler) = CreateClient();
        handler.Setup(async _ =>
        {
            await Task.Delay(5000, CancellationToken.None); // Simulate slow response
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var cts = new CancellationTokenSource(50); // Cancel after 50ms

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetQueueAsync(cts.Token));
    }

    #endregion

    #region Network Error Retry

    [Fact]
    public async Task Network_Error_Retries_Then_Throws()
    {
        var (client, handler) = CreateClient();
        var callCount = 0;
        handler.Setup((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
        {
            callCount++;
            throw new HttpRequestException("Connection refused");
        }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetQueueAsync(CancellationToken.None));

        Assert.Contains("Connection refused", ex.Message);
        Assert.Equal(3, callCount);
    }

    #endregion

    private sealed class StatusPayloadCapture
    {
        public string State { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public long? BytesDownloaded { get; set; }
        public long? SizeBytes { get; set; }
    }

    /// <summary>
    /// Fake HttpMessageHandler that delegates to a configurable handler function.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, Task<HttpResponseMessage>>? _handler;

        public void Setup(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = req => Task.FromResult(handler(req));
        }

        public void Setup(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_handler is null)
                throw new InvalidOperationException("No handler configured");
            return _handler(request);
        }
    }
}
