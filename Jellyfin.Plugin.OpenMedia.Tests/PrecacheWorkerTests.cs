using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Api;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class PrecacheWorkerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _strmDir;
    private readonly ILogger<PrecacheWorker> _logger;
    private readonly PrecacheStateStore _stateStore;

    private const string TestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MismatchHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TestUserId = "user-123";

    public PrecacheWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmedia-worker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _strmDir = Path.Combine(_tempDir, "strm");
        Directory.CreateDirectory(_strmDir);

        _logger = NullLogger<PrecacheWorker>.Instance;
        _stateStore = new PrecacheStateStore(_tempDir, NullLogger<PrecacheStateStore>.Instance);
    }

    public void Dispose()
    {
        _stateStore.Dispose();
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

    /// <summary>
    /// Testable subclass — overrides all virtual methods with configurable behavior.
    /// Tracks calls to ReportStatusAsync and TriggerLibraryRefresh for assertions.
    /// </summary>
    private sealed class TestablePrecacheWorker : PrecacheWorker
    {
        public Func<CancellationToken, Task<IReadOnlyList<QueueItem>>>? GetQueueFunc { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<ReleaseQueueItem>>>? GetReleaseQueueFunc { get; set; }
        public Func<string, string, string?, long?, long?, CancellationToken, Task>? ReportStatusFunc { get; set; }
        public Func<string, CancellationToken, Task<LibraryItem?>>? ResolveItemFunc { get; set; }
        public Func<string, string, long, string, IProgress<long>?, CancellationToken, Task<DownloadResult>>? DownloadFunc { get; set; }
        public Func<string, string, string>? SignUrlFunc { get; set; }
        public Action? RefreshLibraryAction { get; set; }
        public Func<string>? GetStrmDirFunc { get; set; }
        public Func<string, long, (bool Ok, long FreeBytes, long RequiredBytes)>? DiskQuotaFunc { get; set; }

        // Spy state
        public List<(string Hash, string State, string? Reason, long? Bytes, long? Size)> StatusReports { get; } = new List<(string Hash, string State, string? Reason, long? Bytes, long? Size)>();
        public int LibraryRefreshCount { get; private set; }

        public TestablePrecacheWorker(ILogger<PrecacheWorker> logger, PrecacheStateStore stateStore)
            : base(logger, stateStore) { }

        protected override Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken ct)
            => GetQueueFunc!(ct);

        protected override Task<IReadOnlyList<ReleaseQueueItem>> GetReleaseQueueAsync(CancellationToken ct)
            => GetReleaseQueueFunc != null
                ? GetReleaseQueueFunc(ct)
                : Task.FromResult<IReadOnlyList<ReleaseQueueItem>>(Array.Empty<ReleaseQueueItem>());

        protected override Task ReportStatusAsync(string hash, string state, string? reason, long? bytesDownloaded, long? sizeBytes, CancellationToken ct)
        {
            StatusReports.Add((hash, state, reason, bytesDownloaded, sizeBytes));
            return ReportStatusFunc != null
                ? ReportStatusFunc(hash, state, reason, bytesDownloaded, sizeBytes, ct)
                : Task.CompletedTask;
        }

        protected override Task<LibraryItem?> ResolveLibraryItemAsync(string hash, CancellationToken ct)
            => ResolveItemFunc!(hash, ct);

        protected override Task<DownloadResult> PerformDownloadAsync(string hash, string signedUrl, long expectedSize, string targetFolder, IProgress<long>? progress, CancellationToken ct)
            => DownloadFunc!(hash, signedUrl, expectedSize, targetFolder, progress, ct);

        protected override string CreateSignedUrl(string hash, string userId)
            => SignUrlFunc!(hash, userId);

        protected override void TriggerLibraryRefresh()
        {
            LibraryRefreshCount++;
            RefreshLibraryAction?.Invoke();
        }

        protected override string GetStrmDirectory()
            => GetStrmDirFunc?.Invoke() ?? "/tmp/strm";

        protected override (bool Ok, long FreeBytes, long RequiredBytes) CheckDiskQuota(string folder, long expectedSize)
            => DiskQuotaFunc != null
                ? DiskQuotaFunc(folder, expectedSize)
                : base.CheckDiskQuota(folder, expectedSize);
    }

    private static LibraryItem MakeLibraryItem(
        string hash = TestHash,
        long? tmdbId = 12345,
        string? title = "Test Movie",
        int? year = 2024,
        string? fileSize = "1048576") // 1 MB
    {
        return new LibraryItem(hash, tmdbId, title, year, fileSize, 120, "1080p");
    }

    private static QueueItem MakeQueueItem(string hash = TestHash, string userId = TestUserId)
    {
        return new QueueItem(hash, userId, DateTime.UtcNow);
    }

    #region Happy Path

    [Fact]
    public async Task HappyPath_QueuedToDone()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };

        var finalPath = Path.Combine(_strmDir, "Test Movie (2024) [tmdbid-12345]", $"{TestHash}.mp4");
        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            Task.FromResult(new DownloadResult(TestHash, finalPath, size));

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // State should be Done
        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Done, entry.State);
        Assert.Equal(1048576, entry.SizeBytes);
        Assert.Equal(TestHash, entry.Sha256);

        // Status reports: downloading, done
        Assert.Equal(2, worker.StatusReports.Count);
        Assert.Equal("downloading", worker.StatusReports[0].State);
        Assert.Equal("done", worker.StatusReports[1].State);

        // Library refresh triggered
        Assert.Equal(1, worker.LibraryRefreshCount);
    }

    #endregion

    #region SHA Mismatch

    [Fact]
    public async Task ShaMismatch_FileDeletedAndStateFailed()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };

        // Create the .mp4 file to verify it gets deleted on mismatch
        var targetFolder = Path.Combine(_strmDir, "Test Movie (2024) [tmdbid-12345]");
        Directory.CreateDirectory(targetFolder);
        var finalPath = Path.Combine(targetFolder, $"{TestHash}.mp4");
        await File.WriteAllTextAsync(finalPath, "fake content");

        // Download returns a SHA256 that doesn't match the hash
        var wrongSha = MismatchHash;
        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            Task.FromResult(new DownloadResult(wrongSha, finalPath, size));

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // .mp4 should be deleted
        Assert.False(File.Exists(finalPath));

        // State should be Failed
        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Failed, entry.State);
        Assert.Equal("sha_mismatch", entry.LastError);

        // Status reports: downloading, failed
        Assert.Equal(2, worker.StatusReports.Count);
        Assert.Equal("downloading", worker.StatusReports[0].State);
        Assert.Equal("failed", worker.StatusReports[1].State);
        Assert.Equal("sha_mismatch", worker.StatusReports[1].Reason);

        // Library refresh NOT triggered
        Assert.Equal(0, worker.LibraryRefreshCount);
    }

    #endregion

    #region API Throws — Worker Continues

    [Fact]
    public async Task ApiThrows_WorkerContinuesAndReturnsErrorBackoff()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);

        worker.GetQueueFunc = _ => throw new HttpRequestException("API is down");

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PrecacheWorker.ErrorBackoff, delay);

        // Worker did not crash — no state changes
        var all = await _stateStore.GetAllAsync(CancellationToken.None);
        Assert.Empty(all);
    }

    [Fact]
    public async Task ApiThrows_WithApiClientException_WorkerContinues()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);

        worker.GetQueueFunc = _ => throw new ApiClientException("Forbidden", 403);

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert — 4xx treated same as any exception: backoff, no crash
        Assert.Equal(PrecacheWorker.ErrorBackoff, delay);
    }

    #endregion

    #region Empty Queue

    [Fact]
    public async Task EmptyQueue_ReturnsPollingInterval()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(Array.Empty<QueueItem>());

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // No status reports
        Assert.Empty(worker.StatusReports);
    }

    #endregion

    #region State Persisted After Each Step

    [Fact]
    public async Task StatePersisted_AfterDownloadingAndDone()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        // Track state transitions by capturing state at each ReportStatus call
        var stateAtReport = new List<PrecacheState>();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.RefreshLibraryAction = () => { };

        worker.ReportStatusFunc = async (h, state, reason, bytes, size, ct) =>
        {
            var entry = await _stateStore.GetAsync(h, ct);
            if (entry is not null)
            {
                stateAtReport.Add(entry.State);
            }
        };

        var finalPath = Path.Combine(_strmDir, "Test Movie (2024) [tmdbid-12345]", $"{TestHash}.mp4");
        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            Task.FromResult(new DownloadResult(TestHash, finalPath, size));

        // Act
        await worker.TickAsync(CancellationToken.None);

        // Assert — state captured at report points
        // First report (downloading): state should be Downloading
        Assert.Equal(PrecacheState.Downloading, stateAtReport[0]);

        // Final state should be Done
        var finalEntry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(finalEntry);
        Assert.Equal(PrecacheState.Done, finalEntry.State);
        Assert.Equal(1048576, finalEntry.DownloadedBytes);
        Assert.Equal(1048576, finalEntry.SizeBytes);
    }

    #endregion

    #region Hash Not In Library

    [Fact]
    public async Task HashNotInLibrary_ReportsFailed()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(null); // Not found
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        Assert.Single(worker.StatusReports);
        Assert.Equal("failed", worker.StatusReports[0].State);
        Assert.Equal("hash_not_in_library", worker.StatusReports[0].Reason);
    }

    #endregion

    #region Download Exception — Error Recovery

    [Fact]
    public async Task DownloadThrows_StateSetToFailedAndWorkerContinues()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };

        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            throw new IOException("Network connection reset");

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert — worker returns normal interval (not error backoff)
        // because the error is handled inside ProcessItemAsync
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // State should be Failed
        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Failed, entry.State);
        Assert.Equal("IOException", entry.LastError);

        // Status reports: downloading, failed
        Assert.Equal(2, worker.StatusReports.Count);
        Assert.Equal("downloading", worker.StatusReports[0].State);
        Assert.Equal("failed", worker.StatusReports[1].State);
        Assert.Equal("IOException", worker.StatusReports[1].Reason);
    }

    #endregion

    #region Disk Quota Pre-Check

    [Fact]
    public async Task DiskQuotaFails_StateFailedInsufficientDisk_NoDownloadAttempt()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();
        var downloadCalled = false;

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };

        // Disk quota fails
        worker.DiskQuotaFunc = (folder, size) => (false, 1000, size + DiskQuotaChecker.DefaultSafetyMarginBytes);

        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
        {
            downloadCalled = true;
            return Task.FromResult(new DownloadResult(TestHash, "/fake/path", size));
        };

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // Download should NOT have been called
        Assert.False(downloadCalled, "DownloadAsync should not be called when disk quota fails");

        // State should be Failed
        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Failed, entry.State);
        Assert.Equal("insufficient_disk", entry.LastError);

        // Status reports: failed
        Assert.Single(worker.StatusReports);
        Assert.Equal("failed", worker.StatusReports[0].State);
        Assert.Equal("insufficient_disk", worker.StatusReports[0].Reason);

        // Library refresh NOT triggered
        Assert.Equal(0, worker.LibraryRefreshCount);
    }

    [Fact]
    public async Task DiskQuotaPasses_ProceedsToDownload()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();
        var downloadCalled = false;

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };

        // Disk quota passes
        worker.DiskQuotaFunc = (folder, size) => (true, 1_000_000_000L, size);

        var finalPath = Path.Combine(_strmDir, "Test Movie (2024) [tmdbid-12345]", $"{TestHash}.mp4");
        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
        {
            downloadCalled = true;
            return Task.FromResult(new DownloadResult(TestHash, finalPath, size));
        };

        // Act
        await worker.TickAsync(CancellationToken.None);

        // Assert — download was called and succeeded
        Assert.True(downloadCalled, "DownloadAsync should be called when disk quota passes");

        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Done, entry.State);
    }

    #endregion

    #region ENOSPC Mid-Download

    [Fact]
    public async Task EnospcDuringDownload_PartialFileDeletedAndStateFailed()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };
        worker.DiskQuotaFunc = (folder, size) => (true, 1_000_000_000L, size);

        // Create target folder and a .partial file to verify cleanup
        var targetFolder = Path.Combine(_strmDir, "Test Movie (2024) [tmdbid-12345]");
        Directory.CreateDirectory(targetFolder);
        var partialPath = Path.Combine(targetFolder, $"{TestHash}.mp4.partial");
        await File.WriteAllTextAsync(partialPath, "partial content");

        // Download throws IOException with "disk full" message
        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            throw new IOException("No space left on device: disk full");

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // .partial file should be cleaned up
        Assert.False(File.Exists(partialPath), ".partial file should be deleted on ENOSPC");

        // State should be Failed with disk_full_during_download
        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Failed, entry.State);
        Assert.Equal("disk_full_during_download", entry.LastError);

        // Status reports: downloading, failed
        Assert.Equal(2, worker.StatusReports.Count);
        Assert.Equal("downloading", worker.StatusReports[0].State);
        Assert.Equal("failed", worker.StatusReports[1].State);
        Assert.Equal("disk_full_during_download", worker.StatusReports[1].Reason);

        // Library refresh NOT triggered
        Assert.Equal(0, worker.LibraryRefreshCount);
    }

    [Fact]
    public async Task EnospcWithHResult_PartialFileDeletedAndStateFailed()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };
        worker.DiskQuotaFunc = (folder, size) => (true, 1_000_000_000L, size);

        var targetFolder = Path.Combine(_strmDir, "Test Movie (2024) [tmdbid-12345]");
        Directory.CreateDirectory(targetFolder);
        var partialPath = Path.Combine(targetFolder, $"{TestHash}.mp4.partial");
        await File.WriteAllTextAsync(partialPath, "partial content");

        // Download throws IOException with ERROR_DISK_FULL HResult
        var diskFullEx = new IOException("There is not enough space on the disk.");
        // Set HResult via reflection since the constructor doesn't expose it directly
        typeof(Exception).GetProperty("HResult")?.SetValue(diskFullEx, unchecked((int)0x80070027));

        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            throw diskFullEx;

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert
        Assert.False(File.Exists(partialPath), ".partial file should be deleted on ENOSPC");

        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Failed, entry.State);
        Assert.Equal("disk_full_during_download", entry.LastError);
    }

    [Fact]
    public async Task NonDiskFullIOException_GoesToGenericErrorHandler()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);
        var queueItem = MakeQueueItem();

        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };
        worker.DiskQuotaFunc = (folder, size) => (true, 1_000_000_000L, size);

        // Regular IOException (not disk full)
        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            throw new IOException("Network error during transfer");

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert — should fall through to the generic catch handler
        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Failed, entry.State);
        Assert.Equal("IOException", entry.LastError); // generic handler, not disk_full_during_download
    }

    #endregion

    #region Release Queue — release_requested → released

    [Fact]
    public async Task ReleaseRequested_WithLocalMp4_FileDeletedAndStateReleased()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);

        // Create a "done" entry in the state store with a real .mp4 file
        var mp4Dir = Path.Combine(_strmDir, "ReleaseTest");
        Directory.CreateDirectory(mp4Dir);
        var mp4Path = Path.Combine(mp4Dir, $"{TestHash}.mp4");
        await File.WriteAllTextAsync(mp4Path, "cached video content");

        await _stateStore.UpdateAsync(TestHash, _ => new PrecacheEntry
        {
            State = PrecacheState.Done,
            DownloadedBytes = 1024,
            SizeBytes = 1024,
            LocalPath = mp4Path,
            Sha256 = TestHash,
        }, CancellationToken.None);

        // Release queue returns one item
        var releaseItem = new ReleaseQueueItem(TestHash, TestUserId, DateTime.UtcNow);
        worker.GetReleaseQueueFunc = _ => Task.FromResult<IReadOnlyList<ReleaseQueueItem>>(new[] { releaseItem });
        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(Array.Empty<QueueItem>());
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert — normal polling interval returned
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // .mp4 file should be deleted
        Assert.False(File.Exists(mp4Path), ".mp4 should be deleted on release");

        // State store entry should be removed
        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.Null(entry);

        // Status reported as "released"
        Assert.Single(worker.StatusReports);
        Assert.Equal(TestHash, worker.StatusReports[0].Hash);
        Assert.Equal("released", worker.StatusReports[0].State);
    }

    [Fact]
    public async Task ReleaseRequested_WithoutLocalFile_StillReportsReleased()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);

        // No state store entry — no local file exists (e.g. plugin restart after cache clear)
        var releaseItem = new ReleaseQueueItem(TestHash, TestUserId, DateTime.UtcNow);
        worker.GetReleaseQueueFunc = _ => Task.FromResult<IReadOnlyList<ReleaseQueueItem>>(new[] { releaseItem });
        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(Array.Empty<QueueItem>());
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;

        // Act
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert — worker doesn't crash
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        // Status still reported as "released" (idempotent)
        Assert.Single(worker.StatusReports);
        Assert.Equal("released", worker.StatusReports[0].State);
    }

    [Fact]
    public async Task ReleaseQueueApiDown_WorkerContinuesToNormalQueue()
    {
        // Arrange
        var worker = new TestablePrecacheWorker(_logger, _stateStore);

        // Release queue throws (API down)
        worker.GetReleaseQueueFunc = _ => throw new HttpRequestException("API is down");

        // Normal queue still works
        var queueItem = MakeQueueItem();
        worker.GetQueueFunc = _ => Task.FromResult<IReadOnlyList<QueueItem>>(new[] { queueItem });
        worker.ResolveItemFunc = (hash, _) => Task.FromResult<LibraryItem?>(MakeLibraryItem());
        worker.SignUrlFunc = (hash, userId) => $"https://s3.example.com/{hash}?sig=test";
        worker.GetStrmDirFunc = () => _strmDir;
        worker.ReportStatusFunc = (h, s, r, b, sz, _) => Task.CompletedTask;
        worker.RefreshLibraryAction = () => { };

        var finalPath = Path.Combine(_strmDir, "Test Movie (2024) [tmdbid-12345]", $"{TestHash}.mp4");
        worker.DownloadFunc = (hash, url, size, folder, progress, ct) =>
            Task.FromResult(new DownloadResult(TestHash, finalPath, size));

        // Act — should NOT throw, normal queue still processes
        var delay = await worker.TickAsync(CancellationToken.None);

        // Assert — normal download completed despite release queue failure
        Assert.Equal(PrecacheWorker.PollingInterval, delay);

        var entry = await _stateStore.GetAsync(TestHash, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(PrecacheState.Done, entry.State);
    }

    #endregion

    #region IsDiskFullException Helper Tests

    [Fact]
    public void IsDiskFullException_WithEnospcMessage_ReturnsTrue()
    {
        var ex = new IOException("Write failed: ENOSPC");
        Assert.True(PrecacheWorker.IsDiskFullException(ex));
    }

    [Fact]
    public void IsDiskFullException_WithDiskFullMessage_ReturnsTrue()
    {
        var ex = new IOException("Error: disk full on device");
        Assert.True(PrecacheWorker.IsDiskFullException(ex));
    }

    [Fact]
    public void IsDiskFullException_WithRegularIOException_ReturnsFalse()
    {
        var ex = new IOException("Connection reset by peer");
        Assert.False(PrecacheWorker.IsDiskFullException(ex));
    }

    #endregion
}
