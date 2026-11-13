using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Update;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;

namespace Construct.Agent.Tests.Support;

/// <summary>A device with a temporary home and recorded OS calls; features are switched on per test.</summary>
public sealed class FakePlatform : IDevicePlatform, IAgentLauncher, IDisposable
{
    public FakePlatform()
    {
        Home = Path.Combine(Path.GetTempPath(), "agent-test-home-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Home);
        VsCodeUserDirectory = Path.Combine(Home, "Code", "User");
    }

    public string Name { get; init; } = "fake";

    public string Home { get; }

    public string? VsCodeUserDirectory { get; }

    public string SshExecutable => "ssh";

    public bool DirectAddresses { get; init; }

    public IToastRaiser? Notifications { get; init; }

    public IAudioCapture? Microphone { get; init; }

    public IShareMapper? Shares { get; init; }

    public ConcurrentQueue<string> Opened { get; } = new();

    public ConcurrentQueue<string> Launched { get; } = new();

    public Task OpenAsync(string uri, CancellationToken ct)
    {
        Opened.Enqueue(uri);
        return Task.CompletedTask;
    }

    public Task OpenTerminalAsync(string alias, CancellationToken ct)
    {
        Opened.Enqueue("terminal:" + alias);
        return Task.CompletedTask;
    }

    public bool? StartWithOs { get; private set; } = false;

    public void SetStartWithOs(bool on, string executable) => StartWithOs = on;

    public Task LaunchAsync(string executable, CancellationToken ct)
    {
        Launched.Enqueue(executable);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(Home, true); }
        catch (IOException) { }
    }
}

/// <summary>
/// Engines answering from a route table (<c>"GET vms"</c> → JSON), recording every call. A route that is
/// not in the table answers 404, so a test sees any call it did not expect.
/// </summary>
public sealed class FakeEngines : IDeviceEngines
{
    private readonly ConcurrentDictionary<string, Func<JsonNode?, JsonNode?>> _routes = new();

    public List<EngineView> Engines { get; } = [];

    public ConcurrentQueue<(string Engine, string Call, JsonNode? Body)> Calls { get; } = new();

    public FakeEngines Add(string id, string url = "https://engine.test")
    {
        Engines.Add(new EngineView(id, id, url, new string('A', 64), "engine-" + id, true, false, false));
        return this;
    }

    /// <summary>Answers <c>"{METHOD} {path}"</c> on <paramref name="engine"/>; the handler gets the body.</summary>
    public FakeEngines On(string engine, string call, Func<JsonNode?, JsonNode?> answer)
    {
        _routes[engine + " " + call] = answer;
        return this;
    }

    public FakeEngines On(string engine, string call, string json) => On(engine, call, _ => JsonNode.Parse(json));

    public IReadOnlyList<EngineView> List() => Engines;

    public Task<JsonNode?> SendAsync(string engine, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var call = method.Method + " " + path;
        Calls.Enqueue((engine, call, body?.DeepClone()));
        if (!_routes.TryGetValue(engine + " " + call, out var answer)) throw new AgentFailure(404, "not-found", "No such route on the fake engine: " + call);
        return Task.FromResult(answer(body));
    }

    public string[] CallsTo(string engine) => Calls.Where(c => c.Engine == engine).Select(c => c.Call).ToArray();
}

/// <summary>Supervised processes that start their child once and report it up at once, keeping what was spawned.</summary>
public sealed class SpawningProcesses : IRuntimeProcesses
{
    public ConcurrentQueue<Session> Sessions { get; } = new();

    public ISupervisedProcess Start(Func<CancellationToken, IRunningProcess> spawn, ProcessSupervisionOptions options,
        Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output = null)
    {
        var session = new Session(spawn(CancellationToken.None), changed, output);
        Sessions.Enqueue(session);
        return session;
    }

    public sealed class Session(IRunningProcess child, Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output) : ISupervisedProcess
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IRunningProcess Child { get; } = child;

        public SupervisedProcessState State { get; private set; } = new("up");

        public Task<bool> FirstAttempt => Task.FromResult(true);

        public Task Completion => _done.Task;

        public bool Disposed { get; private set; }

        public Task EmitAsync(string chunk) => output?.Invoke(chunk, CancellationToken.None) ?? Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            State = new("stopped");
            changed(State);
            await Child.DisposeAsync();
            _done.TrySetResult();
        }
    }
}

/// <summary>Device services built from fakes: the real VM links, actions, updater and "update everything".</summary>
public sealed class FakeDevice : IAsyncDisposable
{
    public FakeDevice(string dataDirectory, FakeEngines? engines = null, FakePlatform? platform = null, HttpMessageHandler? channel = null,
        string? running = null, string version = "1.0.0+" + "1111111111111111111111111111111111111111")
    {
        Engines = engines ?? new FakeEngines();
        Platform = platform ?? new FakePlatform();
        Directory.CreateDirectory(dataDirectory);
        Options = new AgentOptions { DataDirectory = dataDirectory, Version = version };
        Settings = new DeviceSettings(Path.Combine(dataDirectory, "device.json"));
        var ssh = new SshClientConfig(Platform);
        Links = new VmLinks(Engines, Platform, ssh, Settings, target =>
        {
            var transport = new FakeSshTransport();
            Transports[target.Engine + "/" + target.Vm] = transport;
            return transport;
        }, Processes, new FakeAudioServerFactory(), new SystemClock(TimeProvider.System), null);
        Install = new AgentInstall(dataDirectory, "construct-agent");
        UpdateEverything? everything = null;
        Updater = new AgentUpdater(Options, Install, Settings, new HttpClient(channel ?? new StaticHandler()), "agentLinux",
            () => everything?.Running != true, Platform, () => Stopped++, () => { }) { Running = running };
        Everything = new UpdateEverything(Engines, Updater, () => { }, TimeProvider.System) { PollInterval = TimeSpan.FromMilliseconds(20), HostUpdateLimit = TimeSpan.FromSeconds(5) };
        everything = Everything;
        Services = new DeviceServices(Platform, Settings, ssh, Links, Updater, Everything);
    }

    public FakeEngines Engines { get; }

    public FakePlatform Platform { get; }

    public AgentOptions Options { get; }

    public DeviceSettings Settings { get; }

    public SpawningProcesses Processes { get; } = new();

    public ConcurrentDictionary<string, FakeSshTransport> Transports { get; } = new();

    public VmLinks Links { get; }

    public AgentInstall Install { get; }

    public AgentUpdater Updater { get; }

    public UpdateEverything Everything { get; }

    public DeviceServices Services { get; }

    public int Stopped { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Platform.Dispose();
    }

    /// <summary>Answers nothing: a test that reaches the network through it fails.</summary>
    public sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(answer?.Invoke(request) ?? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    /// <summary>Waits until <paramref name="done"/> holds.</summary>
    public static async Task WaitAsync(Func<bool> done, string what, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }
}
