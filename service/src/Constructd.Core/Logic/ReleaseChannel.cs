using System.Text.Json;
using System.Text.RegularExpressions;
namespace Constructd.Core.Logic;

/// <summary>One package of a channel release: its asset name and SHA-256.</summary>
public sealed record ChannelPackage(string Asset, string Sha256);

/// <summary>The channel manifest (docs/release-flow.md); other keys of the release manifest are ignored here.</summary>
public sealed record ChannelManifest(string Channel, string Version, string Commit, string GuestPayloadHash,
    IReadOnlyDictionary<string, ChannelPackage> Packages);

/// <summary>
/// Where a channel's manifest lives and how to read it. A channel is <c>stable</c>, <c>nightly</c> or the
/// HTTPS URL of a manifest (a mirror or company server); <c>main</c>, the host setting's value before
/// channels existed, means stable.
/// </summary>
public static partial class ReleaseChannel
{
    public const string NightlyTag = "channel-nightly";

    public static Uri? ManifestUrl(string? channel, string repository) => channel switch
    {
        "stable" or "main" => new($"https://github.com/{repository}/releases/latest/download/manifest.json"),
        "nightly" => new($"https://github.com/{repository}/releases/download/{NightlyTag}/manifest.json"),
        _ when Uri.TryCreate(channel, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.Host.Length > 0 => url,
        _ => null,
    };

    public static bool IsValid(string? channel) => ManifestUrl(channel, "owner/repository") is not null;

    /// <summary>The channel fields of a manifest, or null when one is missing or malformed.</summary>
    public static ChannelManifest? Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var channel = Text(root, "channel");
            var version = Text(root, "version");
            var commit = Text(root, "commit");
            var guest = Text(root, "guestPayloadHash");
            if (string.IsNullOrEmpty(channel) || version is null || !VersionPattern().IsMatch(version) ||
                commit is null || !CommitPattern().IsMatch(commit) || !ManifestRules.Hash(guest) ||
                !root.TryGetProperty("packages", out var list) || list.ValueKind != JsonValueKind.Object)
                return null;
            var packages = new Dictionary<string, ChannelPackage>(StringComparer.Ordinal);
            foreach (var entry in list.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) return null;
                var asset = Text(entry.Value, "asset");
                var sha = Text(entry.Value, "sha256");
                if (string.IsNullOrEmpty(asset) || asset.IndexOfAny(['/', '\\']) >= 0 || asset is "." or ".." || !ManifestRules.Hash(sha)) return null;
                packages[entry.Name] = new(asset, sha!);
            }
            return packages.Count == 0 ? null : new(channel, version, commit, guest!, packages);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\A\d+\.\d+\.\d+\+[0-9a-f]{40}\z")]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"\A[0-9a-f]{40}\z")]
    private static partial Regex CommitPattern();
}
