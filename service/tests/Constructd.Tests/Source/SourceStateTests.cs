using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Tests.Source;

public sealed class SourceStateTests
{
    [Fact]
    public async Task DeleteFailureRetainsReservationAndEnsureRetriesCleanup()
    {
        using var f = new SourceFixture(); var bytes = f.Add(); await f.Cache.EnsureAsync(SourceFixture.Commit, null, default);
        await f.Cache.RequestDeleteAsync(SourceFixture.Commit, false, default); f.Files.FailDelete = true;
        var cleanup = await f.Cache.PruneAsync("admin", null, default);
        Assert.Equal("cleanup-failed:io", Assert.Single(cleanup.Retained).Reason);
        Assert.Equal(bytes.Length, await f.Store.CommittedBytesAsync(default));
        Assert.Equal(SourceState.Deleting, (await f.Store.GetAsync(SourceFixture.Commit, default))!.State);
        Assert.Equal("source-cleanup-pending", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.EnsureAsync(SourceFixture.Commit, null, default))).Code);
        f.Files.FailDelete = false; await f.Cache.EnsureAsync(SourceFixture.Commit, null, default);
        Assert.Equal(2, f.Releases.DownloadCount);
    }
    [Fact]
    public async Task CorruptionWithExistingReaderDefersDeletionUntilHandleCloses()
    {
        using var f = new SourceFixture(); var bytes = f.Add(); await f.Cache.EnsureAsync(SourceFixture.Commit, null, default);
        var opened = await f.Cache.OpenAsync(SourceFixture.Commit, default);
        // Replace catalog hash to simulate corruption without an OS-specific write to an open file.
        var item = (await f.Store.GetAsync(SourceFixture.Commit, default))!;
        await f.Store.UpsertAsync(item with { Sha256 = new string('0',64) }, default);
        Assert.Equal("source-corrupt", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.OpenAsync(SourceFixture.Commit, default))).Code);
        Assert.Equal("source-not-cached", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.OpenAsync(SourceFixture.Commit, default))).Code);
        Assert.True(f.Files.Exists(SourceFixture.Commit, SourceFileKind.Zip));
        using var output = new MemoryStream(); await opened.Stream.CopyToAsync(output); Assert.Equal(bytes, output.ToArray());
        await opened.Stream.DisposeAsync(); opened.Stream.Dispose(); // exactly once
        Assert.False(f.Files.Exists(SourceFixture.Commit, SourceFileKind.Zip));
        Assert.Equal(0, await f.Store.CommittedBytesAsync(default));
    }
}
