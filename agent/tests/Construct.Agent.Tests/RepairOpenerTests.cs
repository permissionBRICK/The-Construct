using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Events;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary>
/// A repair that started on this PC opens T3 Desktop on its thread by itself (design 11.5): the agent follows the
/// engines' repair events, reads each session it has not decided yet and opens the desktop app once, never the browser.
/// </summary>
public sealed class RepairOpenerTests : IAsyncDisposable
{
    private const string After = "?after=9223372036854775807";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-test-repair-" + Guid.NewGuid().ToString("n"));
    private readonly AgentEvents _events = new();
    private readonly FakeDevice _device;
    private readonly RepairOpener _opener;

    public RepairOpenerTests()
    {
        var engines = new FakeEngines().Add("e1", "https://host.example:7462")
            .On("e1", "GET whoami", """{"name":"alice","role":"user"}""")
            .On("e1", "GET vms", """[{"name":"work","owner":"alice","kind":"primary","state":"running","deleting":false}]""")
            .On("e1", "GET vms/work/endpoint", """{"sshHost":"host.example","sshPort":2201,"publicHost":"host.example"}""")
            .On("e1", "GET vms/work/forwards", "[]")
            .On("e1", "GET vms/work/forwards?via=work", "[]");
        _device = new FakeDevice(_directory, engines, new FakePlatform { T3Desktop = true });
        _opener = new RepairOpener(_events, _device.Engines, new VmActions(_device.Links, _device.Platform), TimeProvider.System);
    }

    private async Task StartAsync()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        _opener.Start();
    }

    /// <summary>A session as the engine answers it, started <paramref name="ago"/> before now.</summary>
    private static JsonNode Session(string id, bool startedHere = true, bool thread = true, string state = "working", TimeSpan? ago = null,
        string? environment = "env-1") => new JsonObject
    {
        ["id"] = id, ["vm"] = "work", ["jobId"] = "j1", ["jobKind"] = "provision", ["state"] = state, ["automatic"] = true,
        ["started"] = (DateTimeOffset.UtcNow - (ago ?? TimeSpan.FromSeconds(5))).ToString("O"), ["startedHere"] = startedHere,
        ["agent"] = thread ? "t3" : null, ["via"] = thread ? "t3" : null,
        ["t3Thread"] = thread ? new JsonObject { ["environmentId"] = environment, ["threadId"] = "thread-" + id, ["title"] = "Construct repair: provision failed" } : null,
        ["lines"] = new JsonArray(),
    };

    private void Answer(string id, Func<JsonNode?> answer) => _device.Engines.On("e1", $"GET vms/work/repair/{id}{After}", _ => answer());

    private void Event(string type, string id) => _events.Publish(type, "e1", new JsonObject { ["id"] = id, ["vm"] = "work" });

    private int Reads(string id) => _device.Engines.CallsTo("e1").Count(c => c == $"GET vms/work/repair/{id}{After}");

    /// <summary>Waits until <paramref name="id"/> was read <paramref name="count"/> times.</summary>
    private Task ReadAsync(string id, int count = 1) => FakeDevice.WaitAsync(() => Reads(id) >= count, $"read {count} of {id}");

    /// <summary>
    /// Every event before this one has been handled: a session the opener decided was not read for it. A new session's
    /// read shows it, since the events are handled in order.
    /// </summary>
    private async Task SettledAsync()
    {
        var probe = "probe-" + Guid.NewGuid().ToString("n")[..8];
        Answer(probe, () => Session(probe, startedHere: false));
        Event("repair.started", probe);
        await ReadAsync(probe);
    }

    private string[] Opened => [.. _device.Platform.Opened];

    [Fact]
    public async Task A_repair_started_here_opens_t3_desktop_on_its_thread_once()
    {
        await StartAsync();
        Answer("s1", () => Session("s1"));
        Event("repair.started", "s1");
        await FakeDevice.WaitAsync(() => Opened.Length > 0, "T3 Desktop");

        Event("repair.output", "s1");
        Event("repair.output", "s1");
        await SettledAsync();
        Assert.Equal(["t3-desktop t3code://threads/env-1/thread-s1"], Opened);
        Assert.Equal(1, Reads("s1"));
        // Only the desktop app: no pairing link was minted on the VM.
        Assert.Empty(_device.Transports["e1/work"].Scripts);
    }

    [Fact]
    public async Task Without_a_thread_yet_it_waits_for_the_next_event()
    {
        await StartAsync();
        var thread = false;
        Answer("s1", () => Session("s1", thread: Volatile.Read(ref thread)));
        Event("repair.started", "s1");
        await ReadAsync("s1");
        await SettledAsync();
        Assert.Empty(Opened);

        Volatile.Write(ref thread, true);
        Event("repair.output", "s1");
        await FakeDevice.WaitAsync(() => Opened.Length > 0, "T3 Desktop");
        Assert.Equal(["t3-desktop t3code://threads/env-1/thread-s1"], Opened);
    }

    [Theory]
    [InlineData("not-here")]
    [InlineData("ended")]
    [InlineData("late")]
    public async Task A_session_not_started_here_ended_or_past_the_window_opens_nothing_and_is_not_read_again(string which)
    {
        await StartAsync();
        Answer("s1", () => which switch
        {
            "not-here" => Session("s1", startedHere: false),
            "ended" => Session("s1", state: "ended"),
            // A repair from before the agent started again (a self-update): its thread is first seen too late.
            _ => Session("s1", ago: RepairOpener.Window + TimeSpan.FromMinutes(1)),
        });
        Event("repair.started", "s1");
        await ReadAsync("s1");

        Event("repair.output", "s1");
        await SettledAsync();
        Assert.Empty(Opened);
        Assert.Equal(1, Reads("s1"));
    }

    [Fact]
    public async Task A_repair_that_started_while_the_agent_restarted_still_opens_within_the_window()
    {
        await StartAsync();
        Answer("s1", () => Session("s1", ago: TimeSpan.FromMinutes(12)));
        Event("repair.output", "s1");
        await FakeDevice.WaitAsync(() => Opened.Length > 0, "T3 Desktop");
        Assert.Equal(["t3-desktop t3code://threads/env-1/thread-s1"], Opened);
    }

    [Theory]
    [InlineData(false, "env-1")]
    [InlineData(true, null)]
    public async Task Without_t3_desktop_or_a_thread_it_can_take_nothing_opens_not_even_the_browser(bool desktop, string? environment)
    {
        _device.Platform.T3Desktop = desktop;
        await StartAsync();
        _device.Transports["e1/work"].ScriptHandler = (_, _) => Task.FromResult(new Construct.Companion.Core.Abstractions.ProcessResult(0,
            """{"links":[{"kind":"forwarded","pairUrl":"https://host.example:2300/pair#token=x"}]}"""));
        Answer("s1", () => Session("s1", environment: environment));
        Event("repair.started", "s1");
        await ReadAsync("s1");

        Event("repair.output", "s1");
        await SettledAsync();
        Assert.Empty(Opened);
        Assert.Empty(_device.Transports["e1/work"].Scripts);
        Assert.Equal(1, Reads("s1"));
    }

    [Fact]
    public async Task A_failed_read_is_tried_again_on_the_next_event()
    {
        await StartAsync();
        var fail = true;
        Answer("s1", () => Volatile.Read(ref fail) ? throw new AgentFailure(502, "engine-unreachable", "The engine did not answer.") : Session("s1"));
        Event("repair.started", "s1");
        await ReadAsync("s1");
        await SettledAsync();
        Assert.Empty(Opened);

        Volatile.Write(ref fail, false);
        Event("repair.output", "s1");
        await FakeDevice.WaitAsync(() => Opened.Length > 0, "T3 Desktop");
        Assert.Equal(2, Reads("s1"));
    }

    [Fact]
    public async Task One_session_is_read_once_at_a_time_and_events_meanwhile_read_it_once_more()
    {
        await StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        _device.Engines.OnAsync("e1", $"GET vms/work/repair/s1{After}", async _ =>
        {
            var now = Interlocked.Increment(ref running);
            lock (release) most = Math.Max(most, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            return Session("s1", thread: false);
        });
        Event("repair.started", "s1");
        await ReadAsync("s1");
        Event("repair.output", "s1");
        Event("repair.output", "s1");
        await SettledAsync();
        Assert.Equal(1, Reads("s1"));

        release.SetResult();
        await ReadAsync("s1", 2);
        await SettledAsync();
        Assert.Equal(2, Reads("s1"));
        lock (release) Assert.Equal(1, most);
    }

    public async ValueTask DisposeAsync()
    {
        await _opener.DisposeAsync();
        await _device.DisposeAsync();
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
    }
}
