using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Tests.Source;
public sealed class SourcePruneTests
{
    [Fact]
    public async Task PruneRemovesOldFailuresAndOrphansButNeverReadyItems()
    {
        using var f = new SourceFixture(); f.Add(); await f.Cache.EnsureAsync(SourceFixture.Commit, null, default);
        await f.Store.UpsertAsync(f.Item(SourceState.Failed, SourceFixture.Other), default);
        var orphan = new string('c',40); var path = f.Files.PathFor(orphan, SourceFileKind.Part);
        await File.WriteAllTextAsync(path,"orphan"); File.SetLastWriteTimeUtc(path, f.Clock.UtcNow.AddHours(-2).UtcDateTime);
        f.Clock.Advance(TimeSpan.FromHours(25)); var result = await f.Cache.PruneAsync("system", null, default);
        Assert.Equal("ready", Assert.Single(result.Retained).Reason);
        Assert.Contains(SourceFixture.Other, result.Removed); Assert.Contains(orphan, result.Removed);
        Assert.Single(await f.Store.ListAsync(default)); Assert.False(File.Exists(path));
    }
}
