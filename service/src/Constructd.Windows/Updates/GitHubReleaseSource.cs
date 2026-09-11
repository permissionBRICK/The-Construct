using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
namespace Constructd.Windows.Updates;

public sealed class GitHubReleaseSource(HttpClient client) : IReleaseSource
{
    private readonly ConcurrentDictionary<Uri, long> _allowed = new();
    public async Task<IReadOnlyList<ReleaseDescriptor>> ListHostReleasesAsync(string repository, CancellationToken ct, string? releaseTag = null)
    {
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
            releaseTag is not null && !Regex.IsMatch(releaseTag, "^host-[0-9a-f]{40}$"))
            throw new UpdateException("release-source-invalid-metadata");
        try
        {
            var path = releaseTag is null ? "latest/download" : "download/" + releaseTag;
            using var response = await OpenAsync(new Uri($"https://github.com/{repository}/releases/{path}/manifest.json"), HttpMethod.Get, "release manifest", ct);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (bytes.Length + read > 1024 * 1024) throw new UpdateException("release-source-invalid-metadata");
                bytes.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(bytes.ToArray());
            var row = document.RootElement;
            var commit = row.GetProperty("commit").GetString()!;
            var tag = row.GetProperty("releaseTag").GetString()!;
            var payload = row.GetProperty("payloadAsset").GetString()!;
            if (!Regex.IsMatch(commit, "^[0-9a-f]{40}$") || tag != "host-" + commit ||
                releaseTag is not null && tag != releaseTag || row.GetProperty("repository").GetString() != repository ||
                row.GetProperty("schemaVersion").GetInt32() != 1 || row.GetProperty("ref").GetString() != "refs/heads/main" ||
                payload != $"construct-host-{commit[..7]}-win-x64.zip" ||
                !Regex.IsMatch(row.GetProperty("payloadSha256").GetString()!, "^[0-9a-f]{64}$"))
                throw new UpdateException("release-source-invalid-metadata");
            var baseUrl = $"https://github.com/{repository}/releases/download/{tag}/";
            long size;
            if (row.TryGetProperty("payloadSizeBytes", out var sizeJson)) size = sizeJson.GetInt64();
            else if (releaseTag is not null)
            {
                // Explicit recovery pins also support old host-only releases, without REST.
                using var head = await OpenAsync(new Uri(baseUrl + payload), HttpMethod.Head, "release asset", ct);
                size = head.Content.Headers.ContentLength ?? 0;
            }
            else throw new UpdateException("release-source-invalid-metadata");
            if (size <= 0 || size > 1024L * 1024 * 1024) throw new UpdateException("release-source-invalid-metadata");
            if (releaseTag is null &&
                (row.GetProperty("sourceAsset").GetString() != $"construct-source-{commit}.zip" ||
                 !Regex.IsMatch(row.GetProperty("sourceSha256").GetString()!, "^[0-9a-f]{64}$") ||
                 row.GetProperty("sourceSizeBytes").GetInt64() is <= 0 or > 1073741824))
                throw new UpdateException("release-source-invalid-metadata");
            ReleaseAsset[] assets = [new("manifest.json", new Uri(baseUrl + "manifest.json"), bytes.Length),
                                     new(payload, new Uri(baseUrl + payload), size)];
            foreach (var asset in assets) _allowed[asset.Url] = asset.SizeBytes;
            return [new(tag, commit, row.GetProperty("builtAt").GetDateTimeOffset(), assets)];
        }
        catch (HttpRequestException ex) { throw NetworkFailure(ex, "release manifest"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new UpdateException("release-source-timeout", "Timed out retrieving the GitHub release manifest."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { throw new UpdateException("release-source-invalid-metadata", "GitHub returned an invalid release manifest."); }
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri uri, HttpMethod method, string operation, CancellationToken ct)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.UserAgent.ParseAdd("Construct-Host-Updater/1");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            try
            {
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    uri = response.Headers.Location is { } location ? new Uri(uri, location) : throw new UpdateException("release-source-unreachable");
                    if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                        uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                        throw new UpdateException("release-source-unreachable");
                    response.Dispose();
                    continue;
                }
                RequireSuccess(response, operation);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new UpdateException("release-source-unreachable");
    }
    public async Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<string>? progress, CancellationToken ct)
    {
        if (!_allowed.TryGetValue(asset.Url, out var expected) || expected != asset.SizeBytes) throw new UpdateException("release-source-unreachable");
        try
        {
            using var response = await OpenAsync(asset.Url, HttpMethod.Get, "release asset", ct);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; long total = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            { total += read; if (total > expected) throw new UpdateException("release-source-asset-size-mismatch"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            if (total != expected) throw new UpdateException("release-source-asset-size-mismatch");
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
