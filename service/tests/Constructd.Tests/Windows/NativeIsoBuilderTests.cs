using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Windows.Iso;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Windows;

public sealed class NativeIsoBuilderTests
{
    private const string Tool = @"C:\Construct\.construct-tools\iso\Construct.Iso.exe";
    private const string Key = @"C:\Construct\keys\bootstrap_ed25519.pub";
    private const string Source = @"C:\isos\ubuntu-24.04.3-live-server-amd64.iso";
    private const string Output = @"C:\cache\generic.iso";

    [Fact]
    public async Task GenericMediaUsesNativePathsAndStdinCredentialsAndRecordsExecutableHash()
    {
        var (builder, runner, files, _) = Create();
        const string secret = "quotes'\" spaces $ ä";
        var result = await builder.BuildMediaAsync(new(Output, "construct", secret, Key, "hyperv-kvp"), null, default);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Tool, call.FileName);
        Assert.Equal(["--request-stdin"], call.Arguments);
        using var json = JsonDocument.Parse(call.StandardInput!);
        Assert.Equal(secret, json.RootElement.GetProperty("Password").GetString());
        Assert.Equal(Source, json.RootElement.GetProperty("SourceIso").GetString());
        Assert.Equal(Output, json.RootElement.GetProperty("OutputIso").GetString());
        Assert.Equal(Key, json.RootElement.GetProperty("BootstrapPublicKeyPath").GetString());
        Assert.Equal("hyperv-kvp", json.RootElement.GetProperty("HostnameSource").GetString());
        Assert.Equal("tool-sha", result.BuildScriptSha256);
        Assert.Contains(Tool, files.Hashed);
        Assert.Empty(files.Written);
    }

    [Fact]
    public async Task FailedBuildDoesNotExposeSeedPassword()
    {
        var (builder, runner, _, _) = Create();
        runner.Default = new(2, "", "password: secret-value", false);
        var ex = await Assert.ThrowsAsync<IsoBuildException>(() => builder.BuildMediaAsync(
            new(Output, "construct", "secret-value", Key, "hyperv-kvp"), null, default));
        Assert.Contains("native build exited with 2", ex.Message);
        Assert.DoesNotContain("secret-value", ex.Message);
    }

    [Fact]
    public async Task MissingToolFailsBeforeStartingAProcess()
    {
        var (builder, runner, files, _) = Create();
        files.Sizes.Remove(Tool);
        await Assert.ThrowsAsync<IsoBuildException>(() => builder.BuildMediaAsync(
            new(Output, "construct", "secret", Key, "hyperv-kvp"), null, default));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task SourceChecksumFailurePreventsTheNativeBuild()
    {
        var (builder, runner, _, options) = Create();
        options.Iso.Sha256 = "wrong-checksum";
        await Assert.ThrowsAsync<IsoBuildException>(() => builder.BuildMediaAsync(
            new(Output, "construct", "secret", Key, "hyperv-kvp"), null, default));
        Assert.Empty(runner.Calls);
    }

    private static (NativeIsoBuilder, RecordingProcessRunner, FakeIsoFileSystem, ConstructdOptions) Create()
    {
        var options = PlatformOptions.Create();
        var files = new FakeIsoFileSystem().WithBinary(Tool, sha256: "tool-sha")
            .WithBinary(Source).WithBinary(Output).WithFile(Key, "ssh-ed25519 AAAA test");
        var runner = new RecordingProcessRunner();
        return (new NativeIsoBuilder(runner, files, new FakeIsoDownloader(files), options,
            NullLogger<NativeIsoBuilder>.Instance), runner, files, options);
    }
}
