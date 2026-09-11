using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Tests.Support;
using Constructd.Windows.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Updates;

public sealed class GitHubReleaseSourceTests
{
    [Fact]
    public async Task Rate_limit_details_reach_check_response_and_persisted_stage_failure()
    {
        using var github = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1789138800");
            return response;
        }));
        using var app = new TestApp(configureServices: services => services.AddSingleton<IReleaseSource>(new GitHubReleaseSource(github)));
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var check = await admin.PostAsJsonAsync("/api/v1/host/updates/check", new { });
        Assert.Equal(HttpStatusCode.BadGateway, check.StatusCode);
        var problem = await check.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("release-source-rate-limited", problem.GetProperty("code").GetString());
        Assert.Contains("Limit resets at", problem.GetProperty("detail").GetString());
        var stage = await admin.PostAsJsonAsync("/api/v1/host/updates/stage", new { });
        Assert.Equal(HttpStatusCode.Accepted, stage.StatusCode);
        var accepted = await stage.Content.ReadFromJsonAsync<JsonElement>();
        await foreach (var _ in app.Service<IJobEngine>().SubscribeAsync(accepted.GetProperty("jobId").GetString()!, default)) { }
        var row = (await app.Service<IHostUpdateStore>().GetAsync(accepted.GetProperty("updateId").GetString()!, default))!;
        Assert.Equal(HostUpdateState.StageFailed, row.State);
        Assert.Equal("check", row.Phase);
        Assert.Equal("release-source-rate-limited", row.Error);
    }

    [Theory]
    [InlineData(403, true, "release-source-rate-limited")]
    [InlineData(429, false, "release-source-rate-limited")]
    [InlineData(403, false, "release-source-http-403")]
    [InlineData(502, false, "release-source-http-502")]
    public async Task Metadata_http_failures_preserve_safe_status_and_rate_limit_details(int status, bool exhausted, string code)
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret response body") };
            if (exhausted)
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", "1789138800");
            }
            return response;
        }));
        var error = await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", default));
        Assert.Equal(code, error.Code);
        Assert.Contains($"HTTP {status}", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        if (exhausted) Assert.Contains("Limit resets at", error.Message);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "dns-failed")]
    [InlineData(HttpRequestError.SecureConnectionError, "tls-failed")]
    [InlineData(HttpRequestError.ProxyTunnelError, "proxy-failed")]
    [InlineData(HttpRequestError.ConnectionError, "connection-failed")]
    public async Task Network_errors_are_classified_without_exposing_exception_messages(HttpRequestError reason, string code)
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException(reason, "secret proxy URL")));
        var error = await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", default));
        Assert.Equal("release-source-" + code, error.Code);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Asset_http_failure_is_distinguished_from_metadata_failure()
    {
        using var client = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.Contains("/latest/")
            ? new(HttpStatusCode.OK) { Content = JsonContent.Create(Manifest()) }
            : new(HttpStatusCode.Forbidden)));
        var source = new GitHubReleaseSource(client);
        var releases = await source.ListHostReleasesAsync("owner/repo", default);
        var error = await Assert.ThrowsAsync<UpdateException>(() => source.DownloadAsync(releases[0].Assets[0], "unused", null, default));
        Assert.Equal("release-source-http-403", error.Code);
        Assert.Contains("release asset", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_remains_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var client = new HttpClient(new Handler(_ => throw new OperationCanceledException(cancelled.Token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", cancelled.Token));
    }

    private static object Manifest(string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") => new
    {
        schemaVersion = 1, repository = "owner/repo", @ref = "refs/heads/main", commit,
        releaseTag = "host-" + commit, builtAt = DateTimeOffset.UtcNow,
        payloadAsset = $"construct-host-{commit[..7]}-win-x64.zip", payloadSha256 = new string('a', 64), payloadSizeBytes = 123,
        sourceAsset = $"construct-source-{commit}.zip", sourceSha256 = new string('b', 64), sourceSizeBytes = 234
    };

    [Theory]
    [InlineData(null)]
    [InlineData("host-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Discovery_uses_direct_manifest_and_pins_every_asset(string? pin)
    {
        var urls = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            urls.Add(request.RequestUri!.ToString());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Manifest()) };
        }));
        var source = new GitHubReleaseSource(client);
        var releases = await source.ListHostReleasesAsync("owner/repo", default, pin);
        Assert.Single(releases);
        Assert.Equal($"https://github.com/owner/repo/releases/{(pin is null ? "latest/download" : "download/" + pin)}/manifest.json", Assert.Single(urls));
        Assert.All(releases[0].Assets, asset => Assert.StartsWith("https://github.com/owner/repo/releases/download/host-" + new string('a', 40) + "/", asset.Url.ToString()));
        Assert.Contains(releases[0].Assets, a => a.SizeBytes == 123);
    }

    [Fact]
    public async Task Legacy_recovery_pin_uses_asset_head_without_rest()
    {
        var row = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Manifest()))!.AsObject();
        row.Remove("payloadSizeBytes"); row.Remove("sourceAsset"); row.Remove("sourceSha256"); row.Remove("sourceSizeBytes");
        var requests = new List<HttpMethod>();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("github.com", request.RequestUri!.Host);
            Assert.Contains("/download/host-", request.RequestUri.AbsolutePath);
            requests.Add(request.Method);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.Method == HttpMethod.Head)
            {
                response.Content = new ByteArrayContent([]);
                response.Content.Headers.ContentLength = 123;
            }
            else response.Content = new StringContent(row.ToJsonString());
            return response;
        }));
        var release = Assert.Single(await new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", default, "host-" + new string('a', 40)));
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Head }, requests);
        Assert.Contains(release.Assets, a => a.SizeBytes == 123);
    }

    [Fact]
    public async Task Explicit_pin_rejects_a_different_manifest_commit()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(Manifest()) }));
        var error = await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", default, "host-" + new string('b', 40)));
        Assert.Equal("release-source-invalid-metadata", error.Code);
    }

    [Theory]
    [InlineData("repository", "evil/repo")]
    [InlineData("commit", "bad")]
    [InlineData("releaseTag", "moving")]
    [InlineData("payloadAsset", "../secret")]
    [InlineData("sourceSha256", "bad")]
    [InlineData("ref", "refs/heads/dev")]
    public async Task Inconsistent_manifest_is_rejected(string field, string value)
    {
        var row = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Manifest()))!;
        row[field] = value;
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(row.ToJsonString()) }));
        var error = await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", default));
        Assert.Equal("release-source-invalid-metadata", error.Code);
    }

    [Theory]
    [InlineData("http://github.com/unsafe")]
    [InlineData("https://evil.example/manifest.json")]
    [InlineData("https://user@github.com/private")]
    public async Task Manifest_redirects_cannot_escape_trusted_hosts(string target)
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(target);
            return response;
        }));
        await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", default));
    }

    [Fact]
    public async Task Oversized_manifest_is_rejected()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 1024 * 1024 + 1)) }));
        var error = await Assert.ThrowsAsync<UpdateException>(() => new GitHubReleaseSource(client).ListHostReleasesAsync("owner/repo", default));
        Assert.Equal("release-source-invalid-metadata", error.Code);
    }

    [Theory]
    [InlineData("valid", "")]
    [InlineData("missing", "source-unavailable")]
    [InlineData("404", "source-unavailable")]
    [InlineData("hash", "release-source-invalid-metadata")]
    [InlineData("large", "source-too-large")]
    [InlineData("redirect", "release-source-unreachable")]
    public async Task SourceAssetsUseImmutableManifestAndValidateSourceFields(string scenario, string code)
    {
        var row = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Manifest()))!.AsObject();
        if (scenario == "missing") row.Remove("sourceAsset");
        if (scenario == "hash") row["sourceSha256"] = "bad";
        if (scenario == "large") row["sourceSizeBytes"] = 268435457;
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Contains("/download/host-", request.RequestUri!.AbsolutePath);
            if (scenario == "404") return new(HttpStatusCode.NotFound);
            if (scenario == "redirect") { var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://evil.example/file"); return redirect; }
            return new(HttpStatusCode.OK) { Content = new StringContent(row.ToJsonString()) };
        }));
        var source = new GitHubReleaseSource(client);
        if (code.Length == 0)
        {
            var asset = await source.GetSourceAssetAsync("owner/repo", new string('a',40), default);
            Assert.Equal(234, asset.SizeBytes); Assert.EndsWith("construct-source-" + new string('a',40) + ".zip", asset.Url.ToString());
        }
        else
        {
            var error = await Record.ExceptionAsync(() => source.GetSourceAssetAsync("owner/repo", new string('a',40), default));
            Assert.Equal(code, error is SourceException se ? se.Code : Assert.IsType<UpdateException>(error).Code);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
