using System.Text.Json;
using System.Text.RegularExpressions;

namespace Construct.Agent.Setup;

/// <summary>The agent package a channel offers: its version, where to download it and its SHA-256.</summary>
public sealed record SetupPackage(string Version, Uri Url, string Sha256);

/// <summary>A refusal or failure of the setup, in words for the user.</summary>
public sealed class SetupFailure(string message) : Exception(message);

/// <summary>
/// The release channel (docs/release-flow.md), read the way install.ps1 and the agent's own updater read it:
/// <c>stable</c> is GitHub's latest release, <c>nightly</c> the release tagged <c>channel-nightly</c>, an https URL a
/// mirror whose assets lie beside its manifest. The Windows agent is the manifest's <c>packages.agentWindows</c>.
/// </summary>
public static partial class SetupChannel
{
    public const string DefaultRepository = "permissionBRICK/The-Construct";

    public const string PackageKey = "agentWindows";

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[A-Za-z0-9.+_-]{1,100}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    public static bool IsRepository(string repository) => RepositoryPattern().IsMatch(repository);

    /// <summary>A version as install.ps1 accepts it, which is also its folder name under <c>versions\</c>.</summary>
    public static bool IsVersion(string? version) => version is not null and not "." and not ".." && VersionPattern().IsMatch(version);

    /// <summary>The manifest of a channel; null for anything but <c>stable</c>, <c>nightly</c> or an https URL.</summary>
    public static Uri? ManifestUrl(string channel, string repository)
    {
        if (!IsRepository(repository)) return null;
        return channel switch
        {
            "stable" => new($"https://github.com/{repository}/releases/latest/download/manifest.json"),
            "nightly" => new($"https://github.com/{repository}/releases/download/channel-nightly/manifest.json"),
            _ when Uri.TryCreate(channel, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.Host.Length > 0 => url,
            _ => null,
        };
    }

    /// <summary>The Windows agent of a manifest; a manifest without a usable one is refused.</summary>
    public static SetupPackage Package(string manifestJson, Uri manifestUrl, string channel, string repository)
    {
        var refused = new SetupFailure($"The {Name(channel)} channel offers no Construct agent for Windows.");
        JsonDocument document;
        try { document = JsonDocument.Parse(manifestJson); }
        catch (JsonException) { throw refused; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw refused;
            var version = Text(root, "version");
            if (!IsVersion(version)) throw refused;
            if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object
                || !packages.TryGetProperty(PackageKey, out var entry) || entry.ValueKind != JsonValueKind.Object) throw refused;
            var asset = Text(entry, "asset") ?? "";
            var sha = Text(entry, "sha256") ?? "";
            if (asset is "" or "." or ".." || asset.IndexOfAny(['/', '\\']) >= 0 || !Sha256Pattern().IsMatch(sha)) throw refused;
            Uri url;
            if (channel is "stable" or "nightly")
            {
                var tag = Text(root, "releaseTag") ?? "";
                if (!TagPattern().IsMatch(tag)) throw refused;
                url = new Uri($"https://github.com/{repository}/releases/download/{tag}/{Uri.EscapeDataString(asset)}");
            }
            else
            {
                url = new Uri(manifestUrl, Uri.EscapeDataString(asset));
            }

            return new(version!, url, sha.ToLowerInvariant());
        }
    }

    /// <summary>
    /// The engine (host) package the manifest names (<c>payloadAsset</c>, <c>payloadSha256</c>), or null when it names
    /// none usable: the agent's engine step then downloads it itself.
    /// </summary>
    public static SetupPackage? EnginePackage(string manifestJson, Uri manifestUrl, string channel, string repository)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var version = Text(root, "version");
            var asset = Text(root, "payloadAsset") ?? "";
            var sha = Text(root, "payloadSha256") ?? "";
            if (!IsVersion(version) || asset is "" or "." or ".." || asset.IndexOfAny(['/', '\\']) >= 0 || !Sha256Pattern().IsMatch(sha)) return null;
            if (channel is not ("stable" or "nightly")) return new(version!, new Uri(manifestUrl, Uri.EscapeDataString(asset)), sha.ToLowerInvariant());
            var tag = Text(root, "releaseTag") ?? "";
            return TagPattern().IsMatch(tag)
                ? new(version!, new Uri($"https://github.com/{repository}/releases/download/{tag}/{Uri.EscapeDataString(asset)}"), sha.ToLowerInvariant())
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>How a channel is named in messages: a URL channel by its host.</summary>
    public static string Name(string channel) => channel is "stable" or "nightly" ? channel : Uri.TryCreate(channel, UriKind.Absolute, out var url) ? url.Host : "chosen";

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
