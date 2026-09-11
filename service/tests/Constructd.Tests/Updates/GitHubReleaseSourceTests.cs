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
        var tag = "host-" + new string('a', 40);
        var url = $"https://github.com/owner/repo/releases/download/{tag}/manifest.json";
        using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { new
                { tag_name = tag, draft = false, prerelease = false, published_at = DateTimeOffset.UtcNow,
                    assets = new[] { new { name = "manifest.json", browser_download_url = url, size = 123 } } }
                }), Encoding.UTF8, "application/json") }
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

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
