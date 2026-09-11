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
            using var response = await client.SendAsync(request, ct); RequireSuccess(response, "release list");
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
        catch (HttpRequestException ex) { throw NetworkFailure(ex, "release list"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new UpdateException("release-source-timeout", "Timed out retrieving the GitHub release list."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new UpdateException("release-source-invalid-metadata", "GitHub returned an invalid release list."); }
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
                RequireSuccess(response, "release asset");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                { total += read; if (total > expected) throw new UpdateException("release-source-asset-size-mismatch"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                if (total != expected) throw new UpdateException("release-source-asset-size-mismatch");
                return;
            }
            throw new UpdateException("release-source-unreachable");
        }
        catch (HttpRequestException ex) { throw NetworkFailure(ex, "release asset"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new UpdateException("release-source-timeout", "Timed out downloading a GitHub release asset."); }
    }

    // Only emit allowlisted classifications and numeric status/header values. Raw
    // exceptions, response bodies and signed redirect URLs can contain secrets.
    private static void RequireSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        var limited = status == 429 || status == 403 &&
            (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0") ||
             response.Headers.RetryAfter is not null);
        var code = limited ? "release-source-rate-limited" : $"release-source-http-{status}";
        var detail = $"GitHub {operation} returned HTTP {status}.";
        if (limited)
        {
            detail += " GitHub rate limit reached; wait before retrying.";
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
                long.TryParse(resets.FirstOrDefault(), out var reset) && reset is >= 0 and <= 253402300799)
                detail += $" Limit resets at {DateTimeOffset.FromUnixTimeSeconds(reset):yyyy-MM-dd HH:mm:ss} UTC.";
        }
        throw new UpdateException(code, detail);
    }

    private static UpdateException NetworkFailure(HttpRequestException ex, string operation)
    {
        var reason = ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "dns-failed",
            HttpRequestError.SecureConnectionError => "tls-failed",
            HttpRequestError.ProxyTunnelError => "proxy-failed",
            HttpRequestError.ConnectionError => "connection-failed",
            _ => "unreachable"
        };
        return new UpdateException("release-source-" + reason, $"GitHub {operation} failed: {reason}. Check the host service's outbound network access.");
    }
}
