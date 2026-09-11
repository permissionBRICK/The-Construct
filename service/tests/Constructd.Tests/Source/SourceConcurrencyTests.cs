using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Tests.Source;
public sealed class SourceConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameCommitDownloadsOnceAndDifferentCommitsRunInParallel(bool different)
    {
        using var f = new SourceFixture(); f.Add(); f.Add(SourceFixture.Other);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        f.Releases.BeforeDownload = async ct => { if (Interlocked.Increment(ref count) == 2) both.SetResult(); entered.TrySetResult(); await resume.Task.WaitAsync(ct); };
        var first = f.Cache.EnsureAsync(SourceFixture.Commit, null, default); await entered.Task;
        var second = f.Cache.EnsureAsync(different ? SourceFixture.Other : SourceFixture.Commit, null, default);
        if (different) await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else Assert.Equal(1, count);
        resume.SetResult(); await Task.WhenAll(first, second); Assert.Equal(different ? 2 : 1, f.Releases.DownloadCount);
    }
    [Fact]
    public async Task ReaderKeepsFileAndDeleteRefusesNewReaders()
    {
        using var f = new SourceFixture(); f.Add(); await f.Cache.EnsureAsync(SourceFixture.Commit, null, default);
        var opened = await f.Cache.OpenAsync(SourceFixture.Commit, default);
        await f.Cache.RequestDeleteAsync(SourceFixture.Commit, true, default);
        Assert.Equal("busy", Assert.Single((await f.Cache.PruneAsync("admin", null, default)).Retained).Reason);
        Assert.True(f.Files.Exists(SourceFixture.Commit, SourceFileKind.Zip));
        await Assert.ThrowsAsync<SourceException>(() => f.Cache.OpenAsync(SourceFixture.Commit, default));
        await opened.Stream.DisposeAsync(); Assert.False(f.Files.Exists(SourceFixture.Commit, SourceFileKind.Zip));
    }
    [Fact]
    public async Task PinnedCommitNeedsForceAndQueuedEnsureDownloadsAgain()
    {
        using var f = new SourceFixture(); f.Add(); await f.Cache.EnsureAsync(SourceFixture.Commit, null, default);
        await f.Vms.AddAsync(new("vm", "alice", 1, 1, 8, f.Clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, [], SourceCommit: SourceFixture.Commit), 5, default);
        Assert.Equal("source-pinned", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.RequestDeleteAsync(SourceFixture.Commit, false, default))).Code);
        await f.Cache.RequestDeleteAsync(SourceFixture.Commit, true, default);
        await f.Cache.EnsureAsync(SourceFixture.Commit, null, default); Assert.Equal(2, f.Releases.DownloadCount);
    }
    [Fact]
    public async Task CancelledCopyClosesHandleBeforeReleasingItsReaderExactlyOnce()
    {
        await using var app = new Constructd.Tests.Support.TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        SourceEndpointsTests.Add(app);
        var cache = app.Service<Constructd.Api.Source.ISourceCache>();
        await cache.EnsureAsync(SourceFixture.Commit, null, default);
        var opened = await cache.OpenAsync(SourceFixture.Commit, default);
        using var cancelled = new CancellationTokenSource();
        using var destination = new CancelOnWrite(cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opened.Stream.CopyToAsync(destination, cancelled.Token));
        await cache.RequestDeleteAsync(SourceFixture.Commit, true, default);
        Assert.Equal("busy", Assert.Single((await cache.PruneAsync("admin", null, default)).Retained).Reason);
        await opened.Stream.DisposeAsync(); opened.Stream.Dispose();
        Assert.False(app.Service<ISourceFiles>().Exists(SourceFixture.Commit, SourceFileKind.Zip));
        var listing = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<System.Text.Json.JsonElement>(admin, "/api/v1/host/source-cache");
        Assert.Equal(0, listing.GetProperty("items")[0].GetProperty("readers").GetInt32());
        Assert.Equal(SourceState.Failed, (await app.Service<ISourceStore>().GetAsync(SourceFixture.Commit, default))!.State);
    }
    private sealed class CancelOnWrite(CancellationTokenSource source) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { source.Cancel(); return ValueTask.FromCanceled(ct); }
    }

}
