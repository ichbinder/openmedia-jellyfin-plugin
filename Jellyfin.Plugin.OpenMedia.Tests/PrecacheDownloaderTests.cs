using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class PrecacheDownloaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger<PrecacheDownloader> _logger;

    // 64-Zeichen Hex-Hash
    private const string TestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public PrecacheDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-downloader-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logger = NullLogger<PrecacheDownloader>.Instance;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static readonly Random RandomInstance = new(42);

    /// <summary>
    /// Erzeugt Test-Daten mit bekanntem SHA256.
    /// </summary>
    private static (byte[] Data, string Sha256) GenerateTestPayload(int sizeBytes)
    {
        var data = new byte[sizeBytes];
        RandomInstance.NextBytes(data);
        // Deterministisch fuer Tests: Muster wiederholen
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        var sha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return (data, sha256);
    }

    /// <summary>
    /// Erzeugt einen HttpClient mit einem FakeHandler, der die gegebenen Bytes liefert.
    /// </summary>
    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var fakeHandler = new FakeHandler(handler);
        return new HttpClient(fakeHandler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Erzeugt 1 MB Test-Payload und berechnet SHA256.
    /// </summary>
    private static (byte[] Data, string Sha256) GenerateOneMb()
    {
        return GenerateTestPayload(1024 * 1024);
    }

    [Fact]
    public async Task HappyPath_1Mb_DownloadsAndVerifiesSha256()
    {
        var (data, expectedSha) = GenerateOneMb();

        var client = CreateClient(async (req, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data),
            };
        });

        var downloader = new PrecacheDownloader(client, _logger);
        long reportedBytes = 0;

        var result = await downloader.DownloadAsync(
            TestHash,
            "https://s3.example.com/video.mp4",
            data.Length,
            _tempDir,
            new Progress<long>(b => reportedBytes = b),
            CancellationToken.None);

        Assert.Equal(expectedSha, result.Sha256);
        Assert.Equal(Path.Combine(_tempDir, $"{TestHash}.mp4"), result.FinalPath);
        Assert.True(File.Exists(result.FinalPath));
        Assert.Equal(data.Length, new FileInfo(result.FinalPath).Length);
        Assert.Equal(data.Length, reportedBytes);

        // .partial darf nicht mehr existieren
        Assert.False(File.Exists(Path.Combine(_tempDir, $"{TestHash}.mp4.partial")));
    }

    [Fact]
    public async Task RangeResume_PartialExists_OnlyRestDownloaded()
    {
        var (data, expectedSha) = GenerateOneMb();
        var halfSize = data.Length / 2;

        // Erzeuge existierende .partial mit der ersten Haelfte
        var partialPath = Path.Combine(_tempDir, $"{TestHash}.mp4.partial");
        await File.WriteAllBytesAsync(partialPath, data.Take(halfSize).ToArray());

        var client = CreateClient(async (req, ct) =>
        {
            // Verify Range header was sent
            Assert.NotNull(req.Headers.Range);
            Assert.Equal(halfSize, req.Headers.Range.Ranges.First().From);

            return new HttpResponseMessage(HttpStatusCode.PartialContent) // 206
            {
                Content = new ByteArrayContent(data.Skip(halfSize).ToArray()),
            };
        });

        var downloader = new PrecacheDownloader(client, _logger);

        var result = await downloader.DownloadAsync(
            TestHash,
            "https://s3.example.com/video.mp4",
            data.Length,
            _tempDir,
            progress: null,
            CancellationToken.None);

        Assert.Equal(expectedSha, result.Sha256);
        Assert.Equal(Path.Combine(_tempDir, $"{TestHash}.mp4"), result.FinalPath);
        Assert.True(File.Exists(result.FinalPath));
        Assert.Equal(data.Length, new FileInfo(result.FinalPath).Length);

        // Bytes heruntergeladen = nur die zweite Haelfte
        Assert.Equal(halfSize, result.BytesDownloaded);
    }

    [Fact]
    public async Task ServerIgnoresRange_Returns200_TruncatesAndStartsOver()
    {
        var (data, expectedSha) = GenerateOneMb();

        // Erzeuge .partial mit "falschen" Daten
        var partialPath = Path.Combine(_tempDir, $"{TestHash}.mp4.partial");
        await File.WriteAllBytesAsync(partialPath, Enumerable.Range(0, 512 * 1024).Select(_ => (byte)0xFF).ToArray());

        var client = CreateClient(async (req, ct) =>
        {
            // Server ignoriert Range → 200 mit voller Datei
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data),
            };
        });

        var downloader = new PrecacheDownloader(client, _logger);

        var result = await downloader.DownloadAsync(
            TestHash,
            "https://s3.example.com/video.mp4",
            data.Length,
            _tempDir,
            progress: null,
            CancellationToken.None);

        Assert.Equal(expectedSha, result.Sha256);
        Assert.Equal(data.Length, new FileInfo(result.FinalPath).Length);
        Assert.Equal(data.Length, result.BytesDownloaded);
    }

    [Fact]
    public async Task SizeMismatch_ThrowsIOExceptionAndKeepsPartial()
    {
        var (data, _) = GenerateOneMb();

        var client = CreateClient(async (req, ct) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data),
            };
        });

        var downloader = new PrecacheDownloader(client, _logger);

        // Erwarte 2x die tatsaechliche Groesse → mismatch
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            downloader.DownloadAsync(
                TestHash,
                "https://s3.example.com/video.mp4",
                data.Length * 2, // falsche ExpectedSize
                _tempDir,
                progress: null,
                CancellationToken.None));

        Assert.Contains("Size mismatch", ex.Message);

        // .partial bleibt erhalten, .mp4 existiert nicht
        var partialPath = Path.Combine(_tempDir, $"{TestHash}.mp4.partial");
        var finalPath = Path.Combine(_tempDir, $"{TestHash}.mp4");
        Assert.True(File.Exists(partialPath));
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task CancelMidStream_PartialPreserved_FinalNotCreated()
    {
        // Erzeuge 1 MB Daten und cancel via CancellationToken sofort nach dem Request
        var (data, _) = GenerateOneMb();

        using var cts = new CancellationTokenSource();
        var client = CreateClient(async (req, ct) =>
        {
            // Cancel sobald der Response-Stream startet
            cts.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data),
            };
        });

        var downloader = new PrecacheDownloader(client, _logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(
                TestHash,
                "https://s3.example.com/video.mp4",
                data.Length,
                _tempDir,
                progress: null,
                cts.Token));

        var finalPath = Path.Combine(_tempDir, $"{TestHash}.mp4");

        // .mp4 darf nicht existieren (atomic rename wurde nicht erreicht)
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task ExpectedSizeZero_ThrowsArgumentException()
    {
        var client = CreateClient(async (req, ct) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) });

        var downloader = new PrecacheDownloader(client, _logger);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            downloader.DownloadAsync(
                TestHash,
                "https://s3.example.com/video.mp4",
                0, // invalid
                _tempDir,
                progress: null,
                CancellationToken.None));
    }

    private sealed class FakeHandler : DelegatingHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
            InnerHandler = new HttpClientHandler();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
