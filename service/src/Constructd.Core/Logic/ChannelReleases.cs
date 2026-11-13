using System.Text.Json;
using Constructd.Core.Abstractions;
namespace Constructd.Core.Logic;

/// <summary>
/// Turns a channel manifest (docs/release-flow.md) into the release the host stager installs. The
/// channel manifest is the release manifest with the channel keys added; each host package it names must
/// carry the same asset and SHA-256 in <c>packages</c> as in the release keys, so the hash the stager
/// checks is the channel manifest's. Packages of the GitHub channels are downloaded from the release the
/// manifest names (<c>host-&lt;commit&gt;</c>); those of a URL channel from beside the manifest.
/// </summary>
public static class ChannelReleases
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ChannelRelease Parse(byte[] json, Uri manifestUrl, string repository, bool github)
    {
        static UpdateException Invalid() => new("release-source-invalid-metadata", "The channel manifest is invalid.");
        var channel = ReleaseChannel.Read(System.Text.Encoding.UTF8.GetString(json)) ?? throw Invalid();
        ReleaseManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, Json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        try { ManifestRules.ValidateVariants(manifest); }
        catch (UpdateException) { throw Invalid(); }
        if (manifest.Commit != channel.Commit || manifest.ReleaseTag != "host-" + manifest.Commit || manifest.PayloadSizeBytes is null)
            throw Invalid();
        var assets = new List<ReleaseAsset>();
        Uri At(string asset) => github
            ? new($"https://github.com/{repository}/releases/download/{manifest.ReleaseTag}/{asset}")
            : new(manifestUrl, asset);
        void Add(string package, string? asset, string? sha, long? size)
        {
            if (asset is null) return;
            if (!channel.Packages.TryGetValue(package, out var listed) || listed.Asset != asset || listed.Sha256 != sha || size is not > 0)
                throw Invalid();
            assets.Add(new(asset, At(asset), size.Value));
        }
        assets.Add(new("manifest.json", manifestUrl, json.Length));
        Add("payload", manifest.PayloadAsset, manifest.PayloadSha256, manifest.PayloadSizeBytes);
        Add("frameworkDependent", manifest.FrameworkDependentAsset, manifest.FrameworkDependentSha256, manifest.FrameworkDependentSizeBytes);
        Add("linux", manifest.LinuxAsset, manifest.LinuxSha256, manifest.LinuxSizeBytes);
        return new(channel, new(manifest.ReleaseTag, manifest.Commit, manifest.BuiltAt, assets));
    }
}
