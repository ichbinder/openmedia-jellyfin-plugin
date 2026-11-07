using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.OpenMedia.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OpenMedia.Tests;

public sealed class LibrarySyncRunnerTests
{
    private static readonly StrmSyncResult Movies = new(2, 0, 0, 0, 0, 0, 0);

    [Fact]
    public async Task FailingEpisodeSync_KeepsMovieResult()
    {
        var result = await LibrarySyncRunner.CombineWithEpisodeSyncAsync(
            Movies, () => throw new IOException("read-only"), "/tv", NullLogger.Instance);

        Assert.Equal(Movies, result);
    }

    [Fact]
    public async Task SucceedingEpisodeSync_IsCombined()
    {
        var result = await LibrarySyncRunner.CombineWithEpisodeSyncAsync(
            Movies, () => Task.FromResult(new StrmSyncResult(1, 0, 0, 0, 0, 0, 0)), "/tv", NullLogger.Instance);

        Assert.Equal(3, result.Added);
    }

    [Fact]
    public async Task CancelledEpisodeSync_Propagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => LibrarySyncRunner.CombineWithEpisodeSyncAsync(
            Movies, () => throw new OperationCanceledException(), "/tv", NullLogger.Instance));
    }
}
