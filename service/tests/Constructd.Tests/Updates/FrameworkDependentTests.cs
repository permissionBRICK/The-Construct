using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.Updates;

namespace Constructd.Tests.Updates;

public sealed class FrameworkDependentTests
{
    private static ReleaseManifest WithFdd(ReleaseManifest sc, ReleaseManifest fdd, byte[] zip) => sc with
    {
        FrameworkDependentAsset = "construct-host-aaaaaaa-win-x64-fdd.zip",
        FrameworkDependentSha256 = fdd.PayloadSha256,
        FrameworkDependentSizeBytes = zip.Length,
        FrameworkDependentSumsSha256 = fdd.SumsSha256,
        FrameworkDependentUncompressedSizeBytes = Total(zip),
        Runtimes = [new("Microsoft.NETCore.App", 10), new("Microsoft.AspNetCore.App", 10)]
    };
    private static long Total(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        return zip.Entries.Sum(e => e.Length);
    }
    [Theory]
    [InlineData("complete", "framework-dependent")]
    [InlineData("partial", "self-contained")]
    [InlineData("missing", "self-contained")]
    [InlineData("absent", "self-contained")]
    [InlineData("wrong-major", "self-contained")]
    [InlineData("preview", "self-contained")]
    [InlineData("timeout", "self-contained")]
    [InlineData("legacy", "self-contained")]
    public async Task Selects_download_and_reverifies_the_chosen_variant(string scenario, string expected)
    {
        using var fixture = new PackageTests();
        var (sc, scZip) = fixture.Package(); var (fdd, fddZip) = fixture.Package("service/fdd.dll");
        var manifest = scenario == "legacy" ? sc : WithFdd(sc, fdd, fddZip);
        var source = new FakeReleaseSource();
        var assets = new[] { ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateFiles.Json)),
            (sc.PayloadAsset, scZip), ("construct-host-aaaaaaa-win-x64-fdd.zip", fddZip) }.Select(a =>
        {
            var uri = new Uri("https://github.com/test/releases/" + a.Item1);
            source.Assets[uri] = a.Item2; return new ReleaseAsset(a.Item1, uri, a.Item2.Length);
        }).ToArray();
        var release = new ReleaseDescriptor(sc.ReleaseTag, sc.Commit, sc.BuiltAt, assets); source.Releases.Add(release);
        var root = Path.Combine(Path.GetTempPath(), "fdd-stage-" + Guid.NewGuid().ToString("n"));
        var runner = new RuntimeRunner(scenario);
        try
        {
            var stager = new PackageStager(source, new InMemoryHostConfigStore(new MutableClock()), new() { DatabasePath = Path.Combine(root, "db") }, new FakeReleaseInfo(), runner);
            var staged = await stager.StageAsync(Guid.NewGuid().ToString("n"), release, null, default);
            Assert.Equal(expected, staged.Source);
            Assert.Equal(expected == "framework-dependent" ? manifest.FrameworkDependentAsset : manifest.PayloadAsset, source.Downloads.Last().Name);
            Assert.Equal(sc.PayloadAsset, staged.Manifest.PayloadAsset); // Legacy fields never change during selection.
            Assert.True(await stager.VerifyStagedAsync(staged, default));
            Assert.Equal(scenario == "legacy" ? 0 : expected == "framework-dependent" ? 2 : 1, runner.Calls);
            if (expected == "framework-dependent")
            {
                runner.Scenario = "missing";
                Assert.False(await stager.VerifyStagedAsync(staged, default));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Both_manifest_shapes_validate_but_partial_fdd_metadata_does_not()
    {
        using var fixture = new PackageTests(); var (sc, zip) = fixture.Package();
        var release = new ReleaseDescriptor(sc.ReleaseTag, sc.Commit, sc.BuiltAt, []);
        ManifestRules.Validate(sc, sc.Repository, release);
        var fdd = WithFdd(sc, sc, zip);
        ManifestRules.Validate(fdd, sc.Repository, release);
        foreach (var broken in new[] { fdd with { FrameworkDependentAsset = "../escape" }, fdd with { Runtimes = [] },
            fdd with { FrameworkDependentSha256 = null }, fdd with { FrameworkDependentSizeBytes = 0 },
            fdd with { FrameworkDependentSumsSha256 = "bad" }, fdd with { FrameworkDependentUncompressedSizeBytes = null },
            fdd with { Runtimes = [new("Microsoft.AspNetCore.App", 10), new("Microsoft.AspNetCore.App", 10)] },
            sc with { PayloadUncompressedSizeBytes = -1 } })
            Assert.Throws<UpdateException>(() => ManifestRules.Validate(broken, sc.Repository, release));
    }
    [Theory]
    [InlineData("total")]
    [InlineData("entry-cap")]
    [InlineData("lying-entry")]
    public void Compressed_size_limits_fail_before_creating_destination_files(string scenario)
    {
        using var fixture = new PackageTests(); var (manifest, zip) = fixture.Package();
        var root = Path.Combine(Path.GetTempPath(), "fdd-bounds-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            if (scenario != "total")
            {
                for (var i = 0; i < zip.Length - 46; i++)
                    if (BitConverter.ToUInt32(zip, i) == 0x02014b50)
                    { BitConverter.GetBytes(scenario == "entry-cap" ? 268435457u : 1u).CopyTo(zip, i + 24); break; }
                manifest = manifest with { PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(zip)) };
            }
            else manifest = manifest with { PayloadUncompressedSizeBytes = 1 };
            File.WriteAllBytes(Path.Combine(root, "package.zip"), zip);
            Assert.Throws<UpdateException>(() => PackageStager.ExtractAndVerify(root, manifest));
            Assert.False(Directory.Exists(Path.Combine(root, "extracted")));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class RuntimeRunner(string scenario) : IProcessRunner
    {
        public string Scenario { get; set; } = scenario;
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout, IProgress<string>? standardOutputLines, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal("dotnet", fileName); Assert.Equal(new[] { "--list-runtimes" }, arguments);
            Assert.Null(standardInput); Assert.Null(standardOutputLines); Assert.Equal(TimeSpan.FromSeconds(15), timeout);
            if (Scenario == "absent") throw new System.ComponentModel.Win32Exception();
            var core = "Microsoft.NETCore.App 10.0.1 [C:\\Program Files\\dotnet\\shared]\r\n";
            var output = Scenario switch { "complete" => core + "Microsoft.AspNetCore.App 10.0.1 [C:\\shared]\r\n", "partial" => core,
                "wrong-major" => core + "Microsoft.AspNetCore.App 11.0.1 [C:\\shared]", "preview" => core + "Microsoft.AspNetCore.App 10.0.0-rc.1 [C:\\shared]", _ => "" };
            return Task.FromResult(new ProcessResult(0, output, "credential-sentinel", Scenario == "timeout"));
        }
    }
}
