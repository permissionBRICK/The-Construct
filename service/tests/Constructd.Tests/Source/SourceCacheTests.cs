using System.Diagnostics;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Tests.Source;

public sealed class SourceCacheTests
{
    [Fact]
    public async Task EnsureCachesOriginalBytesAndEveryOpenVerifiesThem()
    {
        using var f = new SourceFixture(); var bytes = f.Add();
        var item = await f.Cache.EnsureAsync(SourceFixture.Commit, null, default, "job");
        Assert.Equal(SourceState.Ready, item.State); Assert.Equal("job", item.JobId);
        var opened = await f.Cache.OpenAsync(SourceFixture.Commit, default);
        await using (opened.Stream) { using var output = new MemoryStream(); await opened.Stream.CopyToAsync(output); Assert.Equal(bytes, output.ToArray()); }
        await f.Cache.EnsureAsync(SourceFixture.Commit, null, default); Assert.Equal(1, f.Releases.DownloadCount);
        await File.WriteAllBytesAsync(f.Files.PathFor(SourceFixture.Commit, SourceFileKind.Zip), new byte[bytes.Length]);
        Assert.Equal("source-corrupt", (await Assert.ThrowsAsync<SourceException>(() => f.Cache.OpenAsync(SourceFixture.Commit, default))).Code);
        Assert.Equal(SourceState.Failed, (await f.Store.GetAsync(SourceFixture.Commit, default))!.State);
        Assert.False(f.Files.Exists(SourceFixture.Commit, SourceFileKind.Zip));
    }
    [Theory]
    [InlineData("hash", "source-hash-mismatch")]
    [InlineData("missing", "source-unavailable")]
    [InlineData("size", "source-too-large")]
    [InlineData("transfer", "source-transfer-failed")]
    [InlineData("zip", "extraction-refused")]
    public async Task FailureHasBareCodeAndCanRetry(string fault, string code)
    {
        using var f = new SourceFixture(); f.Add();
        if (fault == "hash") f.Releases.SourceAssets[SourceFixture.Commit] = f.Releases.SourceAssets[SourceFixture.Commit] with { Sha256 = new string('0',64) };
        if (fault == "missing") f.Releases.SourceAssets.Clear();
        if (fault == "size") f.Options.HostAdmin.Source.MaxItemBytes = 1;
        if (fault == "transfer") f.Releases.FailDownloads = true;
        if (fault == "zip") f.Add(bytes: [1,2,3]);
        Assert.Equal(code, (await Assert.ThrowsAsync<SourceException>(() => f.Cache.EnsureAsync(SourceFixture.Commit, null, default))).Message);
        Assert.Equal(0, await f.Store.CommittedBytesAsync(default));
        Assert.False(f.Files.Exists(SourceFixture.Commit, SourceFileKind.Part));
        f.Add(); f.Options.HostAdmin.Source.MaxItemBytes = 268435456; f.Releases.FailDownloads = false;
        Assert.Equal(SourceState.Ready, (await f.Cache.EnsureAsync(SourceFixture.Commit, null, default)).State);
    }
    [Theory]
    [InlineData("../escape", 0)]
    [InlineData("/repo-main/file", 0)]
    [InlineData("repo-main/../escape", 0)]
    [InlineData("repo-main/link", 0xa1ff)]
    [InlineData("repo-main/file", 0x41ed)]
    [InlineData("repo-main/dir/", 0x81a4)]
    [InlineData("repo-main/a\\b", 0)]
    public void UnsafeZipEntriesAreRefused(string path, int mode)
    { using var stream = new MemoryStream(SourceFixture.Zip(path, mode)); Assert.Throws<SourceException>(() => SourceZipRules.Validate(stream)); }
    [Theory]
    [InlineData("else-main/file")]
    [InlineData("repo-main/bin/provision.sh/child")]
    [InlineData("repo-main/bin/provision.sh")]
    public void MultipleRootsCollisionsAndDuplicatesAreRefused(string second)
    { using var stream = new MemoryStream(SourceFixture.Zip(second: second)); Assert.Throws<SourceException>(() => SourceZipRules.Validate(stream)); }
    [Fact]
    public void CompressionRatioIsBounded()
    { using var stream = new MemoryStream(SourceFixture.Zip(size: 100000, compression: System.IO.Compression.CompressionLevel.Optimal)); Assert.Throws<SourceException>(() => SourceZipRules.Validate(stream)); }
    [Constructd.Tests.Support.LinuxToolchainFact]
    public async Task RealGitArchiveDirectoriesAndModeZeroFilesAreAccepted()
    {
        using var f = new SourceFixture(); var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root,"Provision-AgentVM.ps1"))) root = Directory.GetParent(root)!.FullName;
        var zip = Path.Combine(f.Files.Root, "fixture.zip");
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardError = true };
        foreach (var arg in new[] { "archive", "--format=zip", "--prefix=The-Construct-main/", "-o", zip, "HEAD" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        using var stream = File.OpenRead(zip); SourceZipRules.Validate(stream);
    }
}
