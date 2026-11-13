using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary>The local API itself: its token, its origin rules, the engine proxy and the event stream.</summary>
public sealed class LocalApiTests
{
    [Fact]
    public async Task The_token_from_the_token_file_is_required_and_other_origins_are_refused()
    {
        var ui = Path.Combine(Path.GetTempPath(), "agent-test-ui-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(ui);
        await File.WriteAllTextAsync(Path.Combine(ui, "index.html"), "<!doctype html><title>ui</title>");
        await using var agent = await TestAgent.StartAsync(ui: ui);
        var endpoint = EndpointFile.Read(agent.Host.Options.EndpointPath)!;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(agent.Host.Options.EndpointPath));
        Assert.Equal(agent.Host.Token, endpoint.Token);
        Assert.Equal(agent.Host.Port, endpoint.Port);

        using var anonymous = new HttpClient { BaseAddress = new Uri(endpoint.BaseUrl + "/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("api/engines/probe", new { address = "https://127.0.0.1:1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/engines/x/api/v1/vms")).StatusCode);
        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "api/state"))
        {
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(wrong)).StatusCode);
        }

        // Health and the UI bundle need no token; the bundle cannot be framed.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("api/health")).StatusCode);
        using (var page = await anonymous.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("<title>ui</title>", await page.Content.ReadAsStringAsync());
            Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        }

        // A page on another origin is refused even with the token; so is a request for another host name.
        foreach (var origin in new[] { "https://evil.example", "http://127.0.0.1:1", "null" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/state");
            request.Headers.Add("Origin", origin);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.Api.SendAsync(request)).StatusCode);
        }

        using (var same = new HttpRequestMessage(HttpMethod.Get, "api/state"))
        {
            same.Headers.Add("Origin", endpoint.BaseUrl);
            Assert.Equal(HttpStatusCode.OK, (await agent.Api.SendAsync(same)).StatusCode);
        }

        using (var rebound = new HttpRequestMessage(HttpMethod.Get, "api/state"))
        {
            rebound.Headers.Host = $"attacker.example:{endpoint.Port}";
            Assert.Equal(HttpStatusCode.MisdirectedRequest, (await agent.Api.SendAsync(rebound)).StatusCode);
        }

        // A preflight from another origin is never granted.
        using (var preflight = new HttpRequestMessage(HttpMethod.Options, "api/state"))
        {
            preflight.Headers.Add("Origin", "https://evil.example");
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization");
            using var answer = await anonymous.SendAsync(preflight);
            Assert.False(answer.Headers.Contains("Access-Control-Allow-Origin"));
        }

        Directory.Delete(ui, true);
    }

    [Fact]
    public async Task A_proxied_call_carries_the_device_proof_and_only_to_the_pinned_engine()
    {
        using var engine = new EngineHost();
        var signIn = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop" });
        var deviceId = (await laptop.GetAsync("api/state"))["device"]!["id"]!.GetValue<string>();

        // Record contents are served to a device token with its proof only (the user's sign-in gets 403).
        var record = await laptop.GetAsync($"api/engines/{engineId}/api/v1/sync/records/device%3A{deviceId}");
        Assert.Equal("laptop", record["value"]!["name"]!.GetValue<string>());
        using (var signedIn = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(engine.Url) })
        {
            signedIn.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signIn);
            Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.GetAsync($"api/v1/sync/records/device%3A{deviceId}")).StatusCode);
        }

        // What the UI sends as a credential is not passed on: the engine sees the device, not this header.
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"api/engines/{engineId}/api/v1/whoami"))
        {
            request.Headers.Add("X-Construct-Device-Token", "forged");
            using var answer = await laptop.Api.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        }

        // Paths that leave the engine's API are refused.
        Assert.Equal(HttpStatusCode.NotFound, (await laptop.Api.GetAsync($"api/engines/{engineId}/vault/")).StatusCode);
        using (var dotted = await laptop.Api.GetAsync($"api/engines/{engineId}/api/v1/%2e%2e/%2e%2e/vault/"))
            Assert.True(dotted.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, dotted.StatusCode.ToString());

        // An engine whose certificate is not the pinned one gets nothing: the TLS handshake fails before any header is sent.
        await using var other = await TestAgent.StartAsync();
        var added = await other.PostAsync("api/engines", new { address = engine.Url, pin = new string('A', 64), userToken = signIn });
        using (var refused = await other.Api.GetAsync($"api/engines/{added["id"]!.GetValue<string>()}/api/v1/vms"))
        {
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.Contains("engine-unreachable", await refused.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await laptop.Api.GetAsync("api/engines/unknown/api/v1/vms")).StatusCode);
    }

    [Fact]
    public async Task The_event_stream_delivers_an_engine_jobs_progress_and_its_end_once()
    {
        using var engine = new EngineHost();
        var signIn = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop" });

        using var stream = await laptop.Api.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
        var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());

        using var created = await laptop.Api.PostAsJsonAsync($"api/engines/{engineId}/api/v1/vms", new { name = "work-vm", cpu = 2, ramGb = 4, diskGb = 40 });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var jobId = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>();
        Assert.Equal($"/api/engines/{engineId}/api/v1/jobs/{jobId}", created.Headers.Location!.OriginalString);

        var progress = 0;
        var ends = 0;
        string? terminal = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = ReadEventsAsync(reader, timeout.Token).GetAsyncEnumerator(timeout.Token);
        while (terminal is null && await events.MoveNextAsync())
        {
            var (type, data) = events.Current;
            if (data?["data"]?["jobId"]?.GetValue<string>() != jobId) continue;
            Assert.Equal(engineId, data["engine"]!.GetValue<string>());
            if (type == "job.progress") progress++;
            if (type == "job.state")
            {
                ends++;
                terminal = data["data"]!["job"]!["state"]!.GetValue<string>();
            }
        }

        Assert.True(progress > 0, "No progress line arrived.");
        Assert.Equal("succeeded", terminal);

        // A UI that reconnects from the start gets the same job events once more, in order, and no second end.
        using var replay = await laptop.Api.GetAsync("api/events?since=0", HttpCompletionOption.ResponseHeadersRead);
        var replayed = ReadEventsAsync(new StreamReader(await replay.Content.ReadAsStreamAsync()), CancellationToken.None).GetAsyncEnumerator();
        var replayedProgress = 0;
        while (await replayed.MoveNextAsync())
        {
            var (type, data) = replayed.Current;
            if (data?["data"]?["jobId"]?.GetValue<string>() != jobId) continue;
            if (type == "job.progress") replayedProgress++;
            if (type == "job.state") break;
        }

        Assert.Equal(progress, replayedProgress);
        Assert.Equal(1, ends);
    }

    private static async IAsyncEnumerable<(string Type, JsonNode? Data)> ReadEventsAsync(StreamReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? type = null;
        string? data = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            else if (line.Length == 0 && type is not null)
            {
                yield return (type, data is null ? null : JsonNode.Parse(data));
                (type, data) = (null, null);
            }
        }
    }
}
