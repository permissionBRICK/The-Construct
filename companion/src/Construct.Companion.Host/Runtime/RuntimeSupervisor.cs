using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Runtime;

// One runtime per registry entry. A retarget (definition change) needs the instance's dispatch
// lease so an in-flight command keeps its target; a busy lease is retried shortly after.
public sealed class RuntimeSupervisor(IRuntimeRegistry registry, Func<RuntimeInstance, InstanceRuntime> createRuntime, RuntimeMessageBus bus, IClock clock,
    Func<string, CancellationToken, Task<IAsyncDisposable?>>? acquireRetarget = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, InstanceRuntime> runtimes = [];
    private IDisposable? watch;
    private Task loop = Task.CompletedTask;
    private readonly object retryGate = new();
    private bool retryScheduled;
    private Task retry = Task.CompletedTask;
    private void ScheduleRetry()
    {
        lock (retryGate)
        {
            if (retryScheduled || stop.IsCancellationRequested) return;
            retryScheduled = true; retry = RetryAsync();
        }
    }
    private async Task RetryAsync()
    {
        try
        {
            await clock.DelayAsync(TimeSpan.FromMilliseconds(250), stop.Token).ConfigureAwait(false);
            lock (retryGate) retryScheduled = false;
            changes.Writer.TryWrite(true);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async Task StartAsync(CancellationToken token = default)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (watch is not null || stop.IsCancellationRequested) return;
            watch = registry.Watch(() => changes.Writer.TryWrite(true)); loop = LoopAsync();
        }
        finally { serial.Release(); }
        await RefreshAsync(token).ConfigureAwait(false);
    }
    private async Task LoopAsync()
    {
        try { await foreach (var _ in changes.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false)) await RefreshAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async Task RefreshAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, token);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            IReadOnlyList<RuntimeInstance> entries;
            try { entries = await registry.ReadAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch { return; } // Atomic replacement may briefly make the file unreadable: retain live runtimes.
            var next = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
            foreach (var name in runtimes.Keys.ToArray())
                if (!next.TryGetValue(name, out var definition) || definition != runtimes[name].Instance)
                {
                    if (definition is not null && runtimes[name].Instance with { HostLabel = definition.HostLabel } == definition)
                    { await runtimes[name].SetHostLabelAsync(definition.HostLabel, linked.Token).ConfigureAwait(false); continue; }
                    await using var lease = acquireRetarget is null ? null : await acquireRetarget(name, linked.Token).ConfigureAwait(false);
                    if (acquireRetarget is not null && lease is null) { ScheduleRetry(); continue; }
                    var old = runtimes[name]; runtimes.Remove(name); await old.DisposeAsync().ConfigureAwait(false); bus.RemoveInstance(name);
                    if (definition is not null) { var replacement = createRuntime(definition); runtimes.Add(name, replacement); replacement.Start(); }
                }
            foreach (var definition in entries)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (runtimes.ContainsKey(definition.Name)) continue;
                var runtime = createRuntime(definition); runtimes.Add(definition.Name, runtime); runtime.Start();
            }
        }
        finally { serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false); watch?.Dispose(); watch = null; await loop.ConfigureAwait(false); await retry.ConfigureAwait(false);
        await serial.WaitAsync().ConfigureAwait(false);
        try { foreach (var runtime in runtimes.Values) await runtime.DisposeAsync().ConfigureAwait(false); runtimes.Clear(); }
        finally { serial.Release(); }
    }
}
