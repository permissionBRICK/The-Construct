using Constructd.Core.Domain;
namespace Constructd.Tests.Source;
public sealed class SourceCapacityTests
{
    [Fact]
    public async Task TwoCommitsCompeteForOneReservationWithoutEviction()
    {
        using var f = new SourceFixture(); var bytes = f.Add(); f.Add(SourceFixture.Other);
        f.Options.HostAdmin.Source.MaxTotalBytes = bytes.Length;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Releases.BeforeDownload = async ct => { entered.TrySetResult(); await resume.Task.WaitAsync(ct); };
        var first = f.Cache.EnsureAsync(SourceFixture.Commit, null, default); await entered.Task;
        Assert.Equal(bytes.Length, await f.Store.CommittedBytesAsync(default));
        Assert.Equal("source-cache-full", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.EnsureAsync(SourceFixture.Other, null, default))).Code);
        resume.SetResult(); await first;
        Assert.Equal("source-cache-full", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.EnsureAsync(SourceFixture.Other, null, default))).Code);
        Assert.Equal(SourceState.Ready, (await f.Store.GetAsync(SourceFixture.Commit, default))!.State);
    }
    [Fact]
    public async Task LowDiskSpaceNeverTakesReservation()
    {
        using var f = new SourceFixture(); f.Add(); f.Files.AvailableBytes = 1L << 30;
        Assert.Equal("insufficient-space", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.EnsureAsync(SourceFixture.Commit, null, default))).Code);
        Assert.Empty(await f.Store.ListAsync(default));
    }
}
