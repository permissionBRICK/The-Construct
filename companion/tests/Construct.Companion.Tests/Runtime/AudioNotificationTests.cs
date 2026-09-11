using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Core.Repatch;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
using static Construct.Companion.Tests.Runtime.ProcessSupervisorTests;
namespace Construct.Companion.Tests.Runtime;

public sealed class AudioNotificationTests
{
    [Fact]
    public async Task ClaimedNotifyEntriesAreDeliveredOnceThroughToastSeamAndMuteIsHonored()
    {
        var ssh = new FakeSshTransport(); var processes = new FakeRuntimeProcesses(); var toasts = new FakeToastRaiser(); var clock = new FakeClock();
        await using var first = new Notifier("dev", ssh, processes, toasts, clock);
        await using var second = new Notifier("dev", ssh, processes, toasts, clock);
        first.Start(); second.Start();
        ssh.Spool.Notifications.Enqueue("{\"body\":\"Build finished\"}");
        // Atomic guest claiming removes each entry from the shared spool before streaming it.
        while (ssh.Spool.Notifications.TryDequeue(out var message)) await processes.Sessions[0].EmitAsync(message + "\n");
        while (ssh.Spool.Notifications.TryDequeue(out var message)) await processes.Sessions[1].EmitAsync(message + "\n");
        Assert.Single(toasts.Toasts); Assert.Contains("construct://open?instance=dev", toasts.Toasts[0].Xml);
        await processes.Sessions[0].EmitAsync("#\n{\"body\":\"par"); await processes.Sessions[0].EmitAsync("tial\"}\n"); Assert.Equal(2, toasts.Toasts.Count);
        toasts.Availability = ToastAvailability.Muted; await processes.Sessions[0].EmitAsync("{\"body\":\"muted\"}\n"); Assert.Equal(2, toasts.Toasts.Count);
        await first.DisposeAsync(); Assert.True(processes.Sessions[0].Disposed);
    }
    [Fact]
    public async Task NotificationsDropStaleEntriesAndSummarizeBurst()
    {
        var processes = new FakeRuntimeProcesses(); var toasts = new FakeToastRaiser(); var clock = new FakeClock(); clock.Advance(TimeSpan.FromHours(3));
        await using var notifier = new Notifier("dev", new FakeSshTransport(), processes, toasts, clock); notifier.Start();
        var lines = string.Join('\n', Enumerable.Range(0, 9).Select(i => new JsonObject { ["body"] = "message " + i, ["ts"] = i == 0 ? 1 : clock.UtcNow.ToUnixTimeMilliseconds() }.ToJsonString()));
        await processes.Sessions[0].EmitAsync(lines + "\n"); Assert.Equal(6, toasts.Toasts.Count);
        Assert.DoesNotContain(toasts.Toasts, t => t.Xml.Contains("message 0", StringComparison.Ordinal));
        Assert.Contains("3 more notifications", toasts.Toasts[^1].Xml);
    }
    [Fact]
    public async Task AudioArmsOnlyOnConnectAndFansOutOneCaptureToTwoInstances()
    {
        var clock = new FakeClock(); var microphone = new StreamingFakeAudioCapture(); await using var shared = new SharedAudioCapture(microphone);
        var servers = new FakeAudioServerFactory(); var one = AudioSsh(); var two = AudioSsh(); var statuses = new List<AudioStatus>();
        await using var a = new AudioSession(one, new SshProcessSupervisor(clock), servers, shared, statuses.Add);
        await using var b = new AudioSession(two, new SshProcessSupervisor(clock), servers, shared);
        var ea = a.EnableAsync(); var eb = b.EnableAsync(); await Eventually(() => one.Tunnels.Count == 1 && two.Tunnels.Count == 1);
        Assert.Equal(8768, one.Tunnels[0].Spec.VmPort); Assert.Equal(TunnelDirection.Reverse, one.Tunnels[0].Spec.Direction);
        Assert.NotEqual(one.Tunnels[0].Spec.LocalPort, two.Tunnels[0].Spec.LocalPort);
        clock.Advance(TimeSpan.FromMilliseconds(1200)); Assert.True(await ea); Assert.True(await eb); Assert.Equal(0, microphone.Starts);
        var c1 = servers.Servers[0].Connect(); var c2 = servers.Servers[1].Connect();
        await Eventually(() => microphone.Active == 1 && a.Status.Capturing && b.Status.Capturing); Assert.Equal(1, microphone.Starts);
        microphone.Emit(1, 2, 3, 4); await Eventually(() => c1.Frames.Count == 1 && c2.Frames.Count == 1);
        Assert.Equal(c1.Frames[0], c2.Frames[0]); await c1.DisposeAsync(); await Eventually(() => !a.Status.Capturing); Assert.Equal(1, microphone.Active);
        await c2.DisposeAsync(); await Eventually(() => microphone.Active == 0 && !b.Status.Capturing);
        Assert.True(await a.DisableAsync()); Assert.True(one.Tunnels[0].Process.Stopped); Assert.True(servers.Servers[0].Disposed);
        Assert.Contains(AudioProtocol.DisableScript(8768), one.Scripts); Assert.False(a.Status.Enabled);
        await b.DisposeAsync(); Assert.Equal(0, microphone.Active); Assert.Equal(0, clock.PendingDelays);
    }
    [Fact]
    public async Task AudioListenerFailureStopsTunnelAndReleasesCapture()
    {
        var clock = new FakeClock(); var ssh = AudioSsh(); var servers = new FakeAudioServerFactory();
        var microphone = new StreamingFakeAudioCapture(); await using var capture = new SharedAudioCapture(microphone);
        await using var audio = new AudioSession(ssh, new SshProcessSupervisor(clock), servers, capture);
        var enable = audio.EnableAsync(); await Eventually(() => ssh.Tunnels.Count == 1);
        clock.Advance(TimeSpan.FromMilliseconds(1200)); Assert.True(await enable);
        var connection = servers.Servers[0].Connect(); await Eventually(() => microphone.Active == 1);
        servers.Servers[0].Fail(); await Eventually(() => !audio.Status.Enabled && microphone.Active == 0 && ssh.Tunnels[0].Process.Stopped && servers.Servers[0].Disposed);
        Assert.True(connection.Closed.IsCompleted); Assert.True(servers.Servers[0].Disposed); Assert.Equal("server-failed", audio.Status.Error);
    }
    private static FakeSshTransport AudioSsh() => new()
    {
        ScriptHandler = (script, token) => Task.FromResult(new ProcessResult(0, script == AudioProtocol.EnableScript() ? "CONSTRUCT_GATE_PATCHED=1\nCONSTRUCT_PORTS_BUSY=8767\n" : ""))
    };
    [Fact]
    public async Task ArrivingConnectionWaitsForPreviousCaptureCleanupThenGetsNewCapture()
    {
        var microphone = new RestartableFakeAudioCapture(); await using var capture = new SharedAudioCapture(microphone);
        var old = new PausedAudioConnection(); await using var first = await capture.AttachAsync(old, CancellationToken.None);
        await Eventually(() => microphone.Sessions.Count == 1); microphone.Sessions[0].Writer.TryComplete();
        await old.Disposing.WaitAsync(TimeSpan.FromSeconds(3));
        var replacement = new FakeAudioConnection(); var attach = capture.AttachAsync(replacement, CancellationToken.None);
        Assert.False(attach.IsCompleted); old.FinishDisposal(); await using var second = await attach;
        await Eventually(() => microphone.Sessions.Count == 2); microphone.Sessions[1].Writer.TryWrite([1, 2]);
        await Eventually(() => replacement.Frames.Count == 1); Assert.False(replacement.Closed.IsCompleted);
    }
    [Fact]
    public async Task SlowAudioPeerIsClosedWithoutBlockingOtherCapture()
    {
        var microphone = new StreamingFakeAudioCapture(); await using var shared = new SharedAudioCapture(microphone);
        var slow = new FakeAudioConnection { Backpressure = true }; var fast = new FakeAudioConnection();
        await using var s = await shared.AttachAsync(slow, CancellationToken.None); await using var f = await shared.AttachAsync(fast, CancellationToken.None);
        await Eventually(() => microphone.Active == 1); microphone.Emit(4, 3, 2, 1); await Eventually(() => slow.Closed.IsCompleted && fast.Frames.Count == 1);
        Assert.False(fast.Closed.IsCompleted); Assert.Equal(1, microphone.Active);
    }
    [Fact]
    public async Task DisposingDuringEnableRollsBackWithoutStartingListenerOrTunnel()
    {
        var finish = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ssh = new FakeSshTransport { ScriptHandler = (script, _) => script == AudioProtocol.EnableScript() ? finish.Task : Task.FromResult(new ProcessResult(0)) };
        var clock = new FakeClock(); var servers = new FakeAudioServerFactory(); await using var capture = new SharedAudioCapture(new StreamingFakeAudioCapture());
        var audio = new AudioSession(ssh, new SshProcessSupervisor(clock), servers, capture);
        var enable = audio.EnableAsync(); var dispose = audio.DisposeAsync().AsTask(); finish.SetResult(new(0, "CONSTRUCT_GATE_PATCHED=1"));
        Assert.False(await enable); await dispose; Assert.Empty(servers.Servers); Assert.Empty(ssh.Tunnels); Assert.Contains(AudioProtocol.DisableScript(), ssh.Scripts);
    }
    [Fact]
    public async Task BusyRaceTriesNextPortAndTunnelLossReleasesCapture()
    {
        var clock = new FakeClock(); var ssh = AudioSsh(); var attempts = 0; ssh.TunnelStarted = p => { if (Interlocked.Increment(ref attempts) == 1) p.Exit(255); };
        var servers = new FakeAudioServerFactory(); var microphone = new StreamingFakeAudioCapture(); await using var capture = new SharedAudioCapture(microphone);
        await using var audio = new AudioSession(ssh, new SshProcessSupervisor(clock), servers, capture);
        var enable = audio.EnableAsync(); await Eventually(() => ssh.Tunnels.Count == 2); clock.Advance(TimeSpan.FromMilliseconds(1200)); Assert.True(await enable);
        Assert.Equal(8769, ssh.Tunnels[1].Spec.VmPort); var connection = servers.Servers[0].Connect(); await Eventually(() => microphone.Active == 1);
        ssh.Tunnels[1].Process.Exit(255); await Eventually(() => !audio.Status.Enabled && microphone.Active == 0 && connection.Closed.IsCompleted);
        Assert.Equal("tunnel-down", audio.Status.Error);
    }
    [Fact]
    public async Task RepatchRepairsOnlyEnabledStockGates()
    {
        var ssh = new FakeSshTransport(); ssh.Spool.ExpectRun("true", new ProcessResult(0));
        ssh.Spool.ExpectRun(GuestScripts.Render("construct-patch-status"), new ProcessResult(0, "CONSTRUCT_PARTIAL_STATUS=stock\nCONSTRUCT_GATE_STATUS=stock\n"));
        ssh.Spool.ExpectRun(GuestScripts.Render("construct-partial-streaming-enable"), new ProcessResult(0, "CONSTRUCT_PARTIAL_PATCHED=1"));
        var result = await new RepatchJob(ssh).RunAsync(true, false); Assert.True(result["repaired"]!["streaming"]!.GetValue<bool>()); Assert.False(result["repaired"]!["mic"]!.GetValue<bool>());
        Assert.Equal(0, ssh.Spool.RemainingSteps); Assert.DoesNotContain(AudioProtocol.EnableScript(), ssh.Scripts);
    }
}
