using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Http;

namespace Construct.Agent.Tests.Support;

/// <summary>
/// One device agent in process: its own directory (file protection), its local API on a loopback port,
/// and a client that carries the token from the token file as the UI would.
/// </summary>
public sealed class TestAgent : IAsyncDisposable
{
    private TestAgent(AgentHost host, string directory)
    {
        Host = host;
        Directory = directory;
        var endpoint = EndpointFile.Read(host.Options.EndpointPath)!;
        Api = new HttpClient { BaseAddress = new Uri(endpoint.BaseUrl + "/") };
        Api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
    }

    public AgentHost Host { get; }

    public string Directory { get; }

    public HttpClient Api { get; }

    /// <param name="device">The device features; by default the real ones over a fake platform with a temporary home.</param>
    public static async Task<TestAgent> StartAsync(int localEnginePort = 1, string? directory = null, string? ui = null,
        Func<DeviceAgent, Action, Device.DeviceServices>? device = null, Engines.IWindowsSignIn? windowsSignIn = null,
        Upgrade.IUpgradePlatform? upgrade = null, string? gitSearchPath = null)
    {
        directory ??= Path.Combine(Path.GetTempPath(), "agent-test-" + Guid.NewGuid().ToString("n"));
        var options = new AgentOptions
        {
            DataDirectory = directory, UiDirectory = ui, Protection = "file", LocalEnginePort = localEnginePort, Platform = new FakePlatform(),
            WindowsSignIn = windowsSignIn, Upgrade = upgrade,
            // No test reaches the machine's own git (and its credentials): an empty directory unless a fake is given.
            GitSearchPath = gitSearchPath ?? Path.Combine(directory, "no-git"),
        };
        var host = AgentHost.Create(options, new DeviceAgent(options) { PairingPoll = TimeSpan.FromMilliseconds(200) }, device);
        await host.StartAsync();
        return new TestAgent(host, directory);
    }

    public async Task<JsonNode> PostAsync(string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Api.PostAsJsonAsync(path, body ?? new { });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"POST {path}: {(int)response.StatusCode} {text}");
        return text.Length > 0 ? JsonNode.Parse(text)! : new JsonObject();
    }

    public async Task<JsonNode> GetAsync(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Api.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"GET {path}: {(int)response.StatusCode} {text}");
        return text.Length > 0 ? JsonNode.Parse(text)! : new JsonObject();
    }

    /// <summary>Probes the engine and adds it with the pin the probe saw, as the UI does after the user confirms.</summary>
    public async Task<string> AddEngineAsync(EngineHost engine, string? userToken = null)
    {
        var probe = await PostAsync("api/engines/probe", new { address = engine.Url });
        Assert.Equal(engine.Pin, probe["pin"]!.GetValue<string>());
        var added = await PostAsync("api/engines", new { address = engine.Url, pin = probe["pin"]!.GetValue<string>(), userToken });
        return added["id"]!.GetValue<string>();
    }

    /// <summary>Waits until <paramref name="done"/> holds for the agent's state.</summary>
    public async Task<JsonNode> WaitForStateAsync(Func<JsonNode, bool> done, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var state = await GetAsync("api/state");
            if (done(state)) return state;
            Assert.True(DateTime.UtcNow < until, "Timed out; last state: " + state.ToJsonString());
            await Task.Delay(100);
        }
    }

    public string StateFileText() => File.ReadAllText(Host.Options.StatePath);

    public static string Json(object value) => JsonSerializer.Serialize(value);

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        await Host.DisposeAsync();
        try { System.IO.Directory.Delete(Directory, true); }
        catch (IOException) { }
    }
}
