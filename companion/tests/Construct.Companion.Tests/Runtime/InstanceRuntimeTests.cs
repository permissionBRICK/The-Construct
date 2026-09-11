using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Core.Repatch;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
using static Construct.Companion.Tests.Runtime.ProcessSupervisorTests;
namespace Construct.Companion.Tests.Runtime;

public sealed class InstanceRuntimeTests
{
    [Fact]
    public async Task RegistryAddRetargetRemoveAreSerializedAndOldRuntimeStops()
    {
        var registry = new FakeRuntimeRegistry { Instances = [new("a", "1", NotificationsEnabled: false), new("b", "1", NotificationsEnabled: false)] };
        var clock = new FakeClock(); var bus = new RuntimeMessageBus(); var created = new List<(InstanceRuntime Runtime, FakeForwardTransport Transport)>();
        await using var capture = new SharedAudioCapture(new StreamingFakeAudioCapture());
        InstanceRuntime Create(RuntimeInstance definition)
        {
            var transport = new FakeForwardTransport(); var ssh = new FakeSshTransport(); var processes = new FakeRuntimeProcesses();
            var runtime = new InstanceRuntime(definition, new FakeRuntimeProbe(), clock,
                changed => new Forwarder(definition.Name, transport, processes, new PortReservations(), clock, changed),
                () => new Notifier(definition.Name, ssh, processes, new FakeToastRaiser(), clock),
                changed => new AudioSession(ssh, processes, new FakeAudioServerFactory(), capture, changed), new RepatchJob(ssh), bus);
            created.Add((runtime, transport)); return runtime;
        }
        await using var supervisor = new RuntimeSupervisor(registry, Create, bus); await supervisor.StartAsync(); await Eventually(() => created.Count == 2 && bus.Snapshot("a").ContainsKey("state"));
        registry.Instances = [registry.Instances[0] with { HostLabel = "pc" }, registry.Instances[1]];
        await supervisor.RefreshAsync(); Assert.Equal(2, created.Count); Assert.False(created[0].Transport.Released); Assert.Equal("pc", created[0].Runtime.Instance.HostLabel);
        registry.Instances = [registry.Instances[0] with { Revision = "2" }, registry.Instances[1]];
        registry.Signal(); registry.Signal(); await supervisor.RefreshAsync(); await Eventually(() => created.Count == 3);
        Assert.True(created[0].Transport.Released); Assert.False(created[1].Transport.Released);
        registry.Error = new System.IO.IOException(); await supervisor.RefreshAsync(); Assert.False(created[1].Transport.Released); registry.Error = null;
        registry.Instances = []; await supervisor.RefreshAsync(); Assert.All(created, c => Assert.True(c.Transport.Released)); Assert.Empty(bus.Snapshot("a"));
        await supervisor.DisposeAsync(); Assert.Equal(0, clock.PendingDelays);
    }
    [Fact]
    public async Task ProbeFastWindowUsesFiveSecondsAndRevertsToThirty()
    {
        var clock = new FakeClock(); var probe = new FakeRuntimeProbe(); var bus = new RuntimeMessageBus(); var ssh = new FakeSshTransport();
        await using var capture = new SharedAudioCapture(new StreamingFakeAudioCapture());
        await using var runtime = new InstanceRuntime(new("dev", "1", ForwardsEnabled: false, NotificationsEnabled: false), probe, clock,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), new RepatchJob(ssh), bus);
        runtime.Start(); await Eventually(() => probe.Calls == 1 && clock.PendingDelays == 2);
        clock.Advance(TimeSpan.FromSeconds(29)); Assert.Equal(1, probe.Calls); clock.Advance(TimeSpan.FromSeconds(1)); await Eventually(() => probe.Calls == 2);
        runtime.BeginFastRefresh(); await Eventually(() => probe.Calls == 3); await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(5)); await Eventually(() => probe.Calls == 4);
        clock.Advance(TimeSpan.FromMinutes(5)); await Eventually(() => probe.Calls == 5); await Task.Delay(10);
        clock.Advance(TimeSpan.FromSeconds(5)); Assert.Equal(5, probe.Calls); clock.Advance(TimeSpan.FromSeconds(25)); await Eventually(() => probe.Calls == 6);
        Assert.Null(bus.Snapshot("dev")["state"].GetProperty("connectedInstance").GetString());
    }
    [Fact]
    public async Task TunnelDownAndManualDisableDoNotAutoEnableOnProbeTicks()
    {
        var clock = new FakeClock(); var ssh = new FakeSshTransport { ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0, "CONSTRUCT_GATE_PATCHED=1")) };
        var processes = new FakeRuntimeProcesses(); var bus = new RuntimeMessageBus();
        await using var capture = new SharedAudioCapture(new StreamingFakeAudioCapture());
        await using var runtime = new InstanceRuntime(new("dev", "1", ForwardsEnabled: false, NotificationsEnabled: false, MicPassthrough: true, RepatchDelaySeconds: 0), new FakeRuntimeProbe(), clock,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            changed => new AudioSession(ssh, processes, new FakeAudioServerFactory(), capture, changed), new RepatchJob(ssh), bus);
        await runtime.ProbeOnceAsync(); Assert.True(runtime.Audio!.Status.Enabled); Assert.Equal("dev", bus.Snapshot("dev")["audio"].GetProperty("instance").GetString());
        processes.Sessions[0].SetState("failed"); await Eventually(() => !runtime.Audio.Status.Enabled);
        for (var i = 0; i < 4; i++) await runtime.ProbeOnceAsync();
        Assert.Single(ssh.Scripts, s => s == AudioProtocol.EnableScript());
        await runtime.SetAudioAsync(true); Assert.True(runtime.Audio!.Status.Enabled);
        await runtime.SetAudioAsync(false); await runtime.ProbeOnceAsync(); Assert.False(runtime.Audio.Status.Enabled);
        Assert.Equal(2, ssh.Scripts.Count(s => s == AudioProtocol.EnableScript()));
        var message = bus.Snapshot("dev")["audio"]; Assert.Equal("dev", message.GetProperty("instance").GetString()); Assert.False(message.TryGetProperty("tunnel", out _));
    }
    [Fact]
    public async Task FailedStartupGetsExactlyOneRepatchAutoArmRetry()
    {
        var clock = new FakeClock(); var attempts = 0;
        var ssh = new FakeSshTransport { ScriptHandler = (script, _) => Task.FromResult(new ProcessResult(script == AudioProtocol.EnableScript() && ++attempts == 1 ? 1 : 0)) };
        var processes = new FakeRuntimeProcesses(); await using var capture = new SharedAudioCapture(new StreamingFakeAudioCapture());
        await using var runtime = new InstanceRuntime(new("dev", "1", ForwardsEnabled: false, NotificationsEnabled: false, MicPassthrough: true, RepatchDelaySeconds: 45), new FakeRuntimeProbe(), clock,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            changed => new AudioSession(ssh, processes, new FakeAudioServerFactory(), capture, changed), new RepatchJob(ssh), new RuntimeMessageBus());
        await runtime.ProbeOnceAsync(); Assert.Null(runtime.Audio); await runtime.ProbeOnceAsync(); Assert.Equal(1, attempts);
        clock.Advance(TimeSpan.FromSeconds(45)); await Eventually(() => runtime.Audio?.Status.Enabled == true); Assert.Equal(2, attempts);
        for (var i = 0; i < 4; i++) await runtime.ProbeOnceAsync(); Assert.Equal(2, attempts);
    }
    [Fact]
    public async Task MessageBusFiltersInstancesAndBoundsSlowSubscribers()
    {
        var bus = new RuntimeMessageBus(); using var a = bus.Subscribe("a");
        bus.Publish("b", new { type = "state", online = true }); bus.Publish("a", new { type = "audio", enabled = true });
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await using var events = a.ReadAsync(token.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync()); Assert.Equal("a", events.Current.Instance); Assert.Equal("message", events.Current.Event);
        Assert.True(bus.Snapshot("b")["state"].GetProperty("online").GetBoolean());
        for (var i = 0; i < 130; i++) bus.Publish("a", new { type = "state", sequence = i });
        for (var i = 0; i < 128; i++) Assert.True(await events.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await events.MoveNextAsync());
    }
}
