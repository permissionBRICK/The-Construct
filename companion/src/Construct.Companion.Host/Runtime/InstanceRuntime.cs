using System.Text.Json.Nodes;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Core.Repatch;
using Construct.Companion.Core.Runtime;
namespace Construct.Companion.Host.Runtime;

public sealed class InstanceRuntime(RuntimeInstance instance, IRuntimeProbe probe, IClock clock,
    Func<Action<JsonObject>, Forwarder> createForwarder, Func<Notifier> createNotifier,
    Func<Action<AudioStatus>, AudioSession> createAudio, RepatchJob repatch, RuntimeMessageBus bus) : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Forwarder? forwarder;
    private Notifier? notifier;
    private AudioSession? audio;
    private bool online, armWanted, manualAudioOff;
    private RuntimeInstance current = instance;
    private long fastUntil;
    private bool started;
    private Task loop = Task.CompletedTask, repatchTask = Task.CompletedTask;
    private CancellationTokenSource? repatchStop;
    public RuntimeInstance Instance => current;
    public Forwarder? Forwarder => forwarder;
    public AudioSession? Audio => audio;
    public void Start()
    { lock (refresh) { if (started || stop.IsCancellationRequested) return; started = true; loop = LoopAsync(); } }
    public void BeginFastRefresh()
    { Interlocked.Exchange(ref fastUntil, (clock.UtcNow + TimeSpan.FromMinutes(5)).UtcTicks); refresh.Writer.TryWrite(true); }
    public void Refresh() => refresh.Writer.TryWrite(true);
    private async Task LoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { await ProbeOnceAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                catch { bus.Publish(current.Name, new { type = "state", online = false, vmState = "unknown", probeError = "runtime-failed", connectedInstance = (string?)null }); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var delay = clock.DelayAsync(TimeSpan.FromSeconds(clock.UtcNow.UtcTicks < Interlocked.Read(ref fastUntil) ? 5 : 30), wait.Token);
                var signal = refresh.Reader.WaitToReadAsync(wait.Token).AsTask();
                await Task.WhenAny(delay, signal).ConfigureAwait(false); await wait.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(delay, signal).ConfigureAwait(false); } catch (OperationCanceledException) { }
                while (refresh.Reader.TryRead(out _)) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async Task ProbeOnceAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            JsonObject state;
            try { state = await probe.ProbeAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch { state = new() { ["online"] = false, ["vmState"] = "unknown", ["probeError"] = "probe-failed" }; }
            linked.Token.ThrowIfCancellationRequested(); state["type"] = "state"; state["connectedInstance"] = null; bus.Publish(current.Name, state);
            var reachable = state.True("online");
            if (!reachable)
            {
                online = false; await StopJobsAsync().ConfigureAwait(false); return;
            }
            if (current.ForwardsEnabled && forwarder is null)
            {
                forwarder = createForwarder(snapshot => bus.Publish(current.Name, new { type = "forwards", instance = current.Name, forwards = ForwardPlanner.ToPanelForwards(snapshot) }));
                await forwarder.SetHostLabelAsync(current.HostLabel, linked.Token).ConfigureAwait(false);
                var outcome = await forwarder.StartAsync(linked.Token).ConfigureAwait(false);
                if (outcome is "unanswered" or "stood-down") { await forwarder.DisposeAsync().ConfigureAwait(false); forwarder = null; }
            }
            if (current.NotificationsEnabled && notifier is null) { notifier = createNotifier(); notifier.Start(); }
            if (!online)
            {
                online = true; armWanted = current.MicPassthrough && !manualAudioOff;
                await ArmAudioAsync(linked.Token).ConfigureAwait(false);
                if (current.RepatchDelaySeconds > 0)
                {
                    repatchStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    repatchTask = RepatchLaterAsync(repatchStop.Token);
                }
            }
        }
        finally { serial.Release(); }
    }
    public async Task SetHostLabelAsync(string hostLabel, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (forwarder is not null) await forwarder.SetHostLabelAsync(hostLabel, stop.Token).ConfigureAwait(false);
            current = current with { HostLabel = hostLabel };
        }
        finally { serial.Release(); }
    }
    public async Task SetAudioAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            manualAudioOff = !enabled; armWanted = enabled;
            if (enabled) await ArmAudioAsync(linked.Token).ConfigureAwait(false);
            else if (audio is not null) await audio.DisableAsync(linked.Token).ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }
    private async Task ArmAudioAsync(CancellationToken token)
    {
        if (!armWanted) return;
        armWanted = false;
        if (audio is not null) await audio.DisposeAsync().ConfigureAwait(false);
        audio = createAudio(status => bus.Publish(current.Name, status.ToMessage(current.Name)));
        if (!await audio.EnableAsync(token).ConfigureAwait(false)) { await audio.DisposeAsync().ConfigureAwait(false); audio = null; }
    }
    private async Task RepatchLaterAsync(CancellationToken token)
    {
        try
        {
            await clock.DelayAsync(TimeSpan.FromSeconds(Math.Clamp(current.RepatchDelaySeconds, 0, 600)), token).ConfigureAwait(false);
            await serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!online) return;
                var plan = RepatchProtocol.PlanStartupActions(current.StreamingOn, current.MicPassthrough && !manualAudioOff, audio?.Status.Enabled == true, audio is not null);
                if (plan.True("runPass"))
                {
                    var result = await repatch.RunAsync(current.StreamingOn, plan.True("passMicOn"), token).ConfigureAwait(false);
                    if (result["repaired"].True("mic") && audio?.Status.Enabled == true) audio.MarkGatePatched();
                }
                if (plan.True("retryAutoArm")) { armWanted = true; await ArmAudioAsync(token).ConfigureAwait(false); }
            }
            finally { serial.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task StopJobsAsync()
    {
        if (repatchStop is not null) await repatchStop.CancelAsync().ConfigureAwait(false);
        // Its delay and semaphore wait both use the cancelled token, so it cannot wait for serial here.
        await repatchTask.ConfigureAwait(false); repatchStop?.Dispose(); repatchStop = null;
        if (forwarder is not null) await forwarder.DisposeAsync().ConfigureAwait(false); forwarder = null;
        if (notifier is not null) await notifier.DisposeAsync().ConfigureAwait(false); notifier = null;
        if (audio is not null) await audio.DisposeAsync().ConfigureAwait(false); audio = null;
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false); await loop.ConfigureAwait(false);
        await serial.WaitAsync().ConfigureAwait(false);
        try { online = false; await StopJobsAsync().ConfigureAwait(false); }
        finally { serial.Release(); }
        await repatchTask.ConfigureAwait(false); repatchStop?.Dispose();
    }
}
