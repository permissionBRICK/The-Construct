using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Iso;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Windows;

public sealed class OnDemandIsoBuilderTests
{
    [Fact]
    public async Task FirstCreateBuildsThenReusesAndRefreshPublishesANewFile()
    {
        var files = new FakeIsoFileSystem();
        var clock = new MutableClock();
        var options = PlatformOptions.Create();
        var catalog = new FileIsoCatalog(files, clock, options.Iso.CacheDir, NullLogger<FileIsoCatalog>.Instance);
        var producer = new FakeIsoMediaBuilder(files);
        using var builder = new OnDemandIsoBuilder(catalog, producer, files, clock, options);
        Task<string> Build(bool refresh = false) => builder.BuildAsync("vm", "construct", "secret", "key", null, default, refresh);

        var first = await Build();
        Assert.Equal(first, await Build());
        Assert.Single(producer.Requests);
        var fresh = await Build(true);
        Assert.NotEqual(first, fresh);
        Assert.True(producer.Requests[1].Redownload);
        Assert.Equal(fresh, catalog.GetCurrent()!.Path);
        Assert.True(files.FileExists(first)); // A VM may still have the original mounted.

        producer.Failure = new IOException("build failed");
        await Assert.ThrowsAsync<IOException>(() => Build(true));
        Assert.Equal(fresh, catalog.GetCurrent()!.Path);
        Assert.False(files.FileExists(producer.Requests.Last().OutputPath));
    }

    [Fact]
    public async Task RedownloadReplacesOnlyChecksumVerifiedSourceAndFailureKeepsTheOldOne()
    {
        var options = PlatformOptions.Create();
        options.Iso.SourcePath = "";
        options.Iso.NativeBuilderPath = @"C:\Construct\tool.exe";
        options.Iso.BootstrapPublicKeyPath = @"C:\Construct\key.pub";
        options.Iso.SourceUrl = "https://example.test/ubuntu.iso";
        options.Iso.Sha256 = "good";
        var source = options.Iso.CacheDir + @"\ubuntu.iso";
        const string output = @"C:\cache\new.iso";
        var files = new FakeIsoFileSystem().WithBinary(source, sha256: "good")
            .WithBinary(options.Iso.NativeBuilderPath).WithBinary(output)
            .WithFile(options.Iso.BootstrapPublicKeyPath, "ssh-ed25519 AAAA test");
        var downloads = new FakeIsoDownloader(files) { DownloadedSha256 = "good" };
        var runner = new RecordingProcessRunner();
        using var builder = new NativeIsoBuilder(runner, files, downloads, options, NullLogger<NativeIsoBuilder>.Instance);
        IsoMediaRequest Request(bool refresh) => new(output, "construct", "secret", options.Iso.BootstrapPublicKeyPath, "hyperv-kvp", refresh);

        await builder.BuildMediaAsync(Request(false), null, default);
        Assert.Empty(downloads.Downloads);
        await builder.BuildMediaAsync(Request(true), null, default);
        Assert.Single(downloads.Downloads);
        Assert.Equal(2, runner.Calls.Count);

        downloads.DownloadedSha256 = "bad";
        await Assert.ThrowsAsync<IsoBuildException>(() => builder.BuildMediaAsync(Request(true), null, default));
        Assert.Equal("good", files.ComputeSha256(source));
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(downloads.Downloads, d => Assert.False(files.FileExists(d.Destination)));
    }
}
