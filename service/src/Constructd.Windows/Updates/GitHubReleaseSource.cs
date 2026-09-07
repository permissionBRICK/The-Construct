using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
namespace Constructd.Windows.Updates;

public sealed class GitHubReleaseSource(HttpClient client) : IReleaseSource
{
    private readonly ConcurrentDictionary<Uri, long> _allowed = new();
    public async Task<IReadOnlyList<ReleaseDescriptor>> ListHostReleasesAsync(string repository, CancellationToken ct)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new UpdateException("release-source-unreachable");
        var result = new List<ReleaseDescriptor>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=100");
            request.Headers.UserAgent.ParseAdd("Construct-Host-Updater/1");
            using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            var rows = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            foreach (var row in rows.EnumerateArray())
            {
                var tag = row.GetProperty("tag_name").GetString()!;
                if (!Regex.IsMatch(tag, "^host-[0-9a-f]{40}$") || row.GetProperty("draft").GetBoolean() || row.GetProperty("prerelease").GetBoolean()) continue;
                var assets = new List<ReleaseAsset>();
                foreach (var asset in row.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString()!;
                    var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
                    if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{repository}/releases/download/{tag}/", StringComparison.Ordinal)) continue;
                    var size = asset.GetProperty("size").GetInt64();
                    if (size <= 0 || size > 1024L * 1024 * 1024) continue;
                    _allowed[uri] = size; assets.Add(new(name, uri, size));
                }
                result.Add(new(tag, tag[5..], row.GetProperty("published_at").GetDateTimeOffset(), assets));
            }
            return result.OrderByDescending(r => r.PublishedAt).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        { throw new UpdateException("release-source-unreachable"); }
    }
    public async Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<string>? progress, CancellationToken ct)
    {
        if (!_allowed.TryGetValue(asset.Url, out var expected) || expected != asset.SizeBytes) throw new UpdateException("release-source-unreachable");
        try
        {
            var uri = asset.Url;
            for (var redirect = 0; redirect < 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("Construct-Host-Updater/1");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    uri = response.Headers.Location is { } location ? new Uri(uri, location) : throw new UpdateException("release-source-unreachable");
                    if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                        uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new UpdateException("release-source-unreachable");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                { total += read; if (total > expected) throw new UpdateException("release-source-unreachable"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                if (total != expected) throw new UpdateException("release-source-unreachable");
                return;
            }
            throw new UpdateException("release-source-unreachable");
        }
        catch (HttpRequestException) { throw new UpdateException("release-source-unreachable"); }
    }
}
