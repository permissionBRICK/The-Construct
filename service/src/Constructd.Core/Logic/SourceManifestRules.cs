using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class SourceManifestRules
{
    public static SourceAssetDescriptor Parse(ReadOnlyMemory<byte> bytes, string repository, string commit, long maxBytes)
    {
        try
        {
            if (bytes.Length > 1024 * 1024 || !SourceZipRules.ValidCommit(commit) || !Regex.IsMatch(repository, "\\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\\z"))
                throw new SourceException("release-source-invalid-metadata");
            using var doc = JsonDocument.Parse(bytes); var row = doc.RootElement;
            var tag = "host-" + commit;
            if (row.GetProperty("commit").GetString() != commit || row.GetProperty("releaseTag").GetString() != tag ||
                row.GetProperty("repository").GetString() != repository || row.GetProperty("schemaVersion").GetInt32() != 1 ||
                row.GetProperty("ref").GetString() != "refs/heads/main" ||
                row.GetProperty("payloadAsset").GetString() != $"construct-host-{commit[..7]}-win-x64.zip" ||
                !Hash(row.GetProperty("payloadSha256").GetString())) throw new SourceException("release-source-invalid-metadata");
            _ = row.GetProperty("builtAt").GetDateTimeOffset();
            if (!row.TryGetProperty("sourceAsset", out var asset) || asset.ValueKind == JsonValueKind.Null) throw new SourceException("source-unavailable");
            var size = row.GetProperty("sourceSizeBytes").GetInt64(); var hash = row.GetProperty("sourceSha256").GetString();
            if (asset.GetString() != $"construct-source-{commit}.zip" || !Hash(hash) || size <= 0) throw new SourceException("release-source-invalid-metadata");
            if (size > maxBytes) throw new SourceException("source-too-large");
            return new(commit, tag, new Uri($"https://github.com/{repository}/releases/download/{tag}/{asset.GetString()}"), size, hash!.ToLowerInvariant());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { throw new SourceException("release-source-invalid-metadata"); }
    }
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z");
}
