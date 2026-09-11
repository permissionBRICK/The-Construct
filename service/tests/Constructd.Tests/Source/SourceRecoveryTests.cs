using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Tests.Source;
public sealed class SourceRecoveryTests
{
    [Theory]
    [InlineData(SourceState.Downloading, false)]
    [InlineData(SourceState.Ready, false)]
    [InlineData(SourceState.Deleting, false)]
    [InlineData(SourceState.Downloading, true)]
    [InlineData(SourceState.Deleting, true)]
    public async Task CrashedRowsKeepChargesUntilFilesAreGone(SourceState state, bool fail)
    {
        using var f = new SourceFixture(); f.Add(); await f.Store.UpsertAsync(f.Item(state), default);
        if (state != SourceState.Ready)
        {
            await File.WriteAllTextAsync(f.Files.PathFor(SourceFixture.Commit, SourceFileKind.Zip), "published before crash");
            await File.WriteAllTextAsync(f.Files.PathFor(SourceFixture.Commit, SourceFileKind.Part), "partial");
        }
        f.Files.FailDelete = fail; await f.Cache.RecoverAsync(default);
        Assert.Equal(fail ? SourceState.Deleting : SourceState.Failed, (await f.Store.GetAsync(SourceFixture.Commit, default))!.State);
        Assert.Equal(fail ? 123 : 0, await f.Store.CommittedBytesAsync(default));
        Assert.Contains(await f.Audit.QueryAsync(100, default), a => a.Action == "source.recover");
        f.Files.FailDelete = false; await f.Cache.EnsureAsync(SourceFixture.Commit, null, default);
        Assert.Equal(1, f.Releases.DownloadCount);
    }
}
