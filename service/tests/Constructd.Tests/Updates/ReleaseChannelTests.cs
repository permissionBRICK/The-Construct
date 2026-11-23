using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Updates;

public sealed class ReleaseChannelTests
{
    private const string Repo = "owner/repo";
    private static readonly string Commit = new('a', 40), Hash = new('b', 64);

    [Theory]
    [InlineData("stable", "https://github.com/owner/repo/releases/latest/download/manifest.json")]
    [InlineData("main", "https://github.com/owner/repo/releases/latest/download/manifest.json")]
    [InlineData("nightly", "https://github.com/owner/repo/releases/download/channel-nightly/manifest.json")]
    [InlineData("https://mirror.example.invalid/construct/manifest.json", "https://mirror.example.invalid/construct/manifest.json")]
    public void A_channel_names_its_manifest_url(string channel, string url) =>
        Assert.Equal(new Uri(url), ReleaseChannel.ManifestUrl(channel, Repo));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("beta")]
    [InlineData("Stable")]
    [InlineData("http://mirror.example.invalid/manifest.json")]
    [InlineData("file:///etc/manifest.json")]
    public void Anything_else_is_not_a_channel(string? channel)
    {
        Assert.Null(ReleaseChannel.ManifestUrl(channel, Repo));
        Assert.False(ReleaseChannel.IsValid(channel));
    }

    [Theory]
    [InlineData("stable", null)]
    [InlineData("main", null)]
    [InlineData("nightly", null)]
    [InlineData("https://mirror.example.invalid/manifest.json", null)]
    [InlineData("beta", "channel")]
    public void The_host_setting_accepts_the_channels(string channel, string? problem)
    {
        var message = HostConfigValidation.Validate(new UpdatesConfig(Repo, channel, 60, 120));
        if (problem is null) Assert.Null(message); else Assert.Contains(problem, message);
    }

    private static object Package(string asset, string sha) => new { asset, sha256 = sha };

    private static string Manifest(string? version = null, string? guest = null, object? packages = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, commit = Commit, channel = "nightly", version = version ?? "1.2.3+" + Commit, guestPayloadHash = guest ?? Hash,
        packages = packages ?? new Dictionary<string, object>
        {
            ["payload"] = Package("construct-host-aaaaaaa-win-x64.zip", Hash), ["source"] = Package($"construct-source-{Commit}.zip", Hash),
        },
    });

    [Fact]
    public void A_manifest_reads_its_channel_fields()
    {
        var manifest = ReleaseChannel.Read(Manifest());
        Assert.NotNull(manifest);
        Assert.Equal(("nightly", "1.2.3+" + Commit, Commit, Hash), (manifest.Channel, manifest.Version, manifest.Commit, manifest.GuestPayloadHash));
        Assert.Equal(new ChannelPackage("construct-host-aaaaaaa-win-x64.zip", Hash), manifest.Packages["payload"]);
        Assert.Equal(2, manifest.Packages.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"commit":"x"}""")]
    public void Malformed_json_reads_as_null(string json) => Assert.Null(ReleaseChannel.Read(json));

    [Fact]
    public void A_manifest_with_a_bad_field_reads_as_null()
    {
        Assert.Null(ReleaseChannel.Read(Manifest(version: "1.2+" + Commit)));
        Assert.Null(ReleaseChannel.Read(Manifest(guest: "B".PadRight(64, 'B'))));
        Assert.Null(ReleaseChannel.Read(Manifest(packages: new { })));
        Assert.Null(ReleaseChannel.Read(Manifest(packages: new { payload = Package("../x.zip", Hash) })));
        Assert.Null(ReleaseChannel.Read(Manifest(packages: new { payload = Package("x.zip", "short") })));
    }
}
