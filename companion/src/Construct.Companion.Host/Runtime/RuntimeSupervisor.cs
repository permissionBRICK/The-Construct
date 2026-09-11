using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Runtime;

public sealed class RuntimeSupervisor(IRuntimeRegistry registry, Func<RuntimeInstance, InstanceRuntime> createRuntime, RuntimeMessageBus bus) : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, InstanceRuntime> runtimes = [];
    private IDisposable? watch;
    private Task loop = Task.CompletedTask;
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
                    var old = runtimes[name]; runtimes.Remove(name); await old.DisposeAsync().ConfigureAwait(false); bus.RemoveInstance(name);
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
        await stop.CancelAsync().ConfigureAwait(false); watch?.Dispose(); watch = null; await loop.ConfigureAwait(false);
        await serial.WaitAsync().ConfigureAwait(false);
        try { foreach (var runtime in runtimes.Values) await runtime.DisposeAsync().ConfigureAwait(false); runtimes.Clear(); }
        finally { serial.Release(); }
    }
}

// State's registry parser/projector remains the single owner of its normalization rules.
public sealed class FileRuntimeRegistry(IFileSystem files, string directory,
    Func<CancellationToken, Task<IReadOnlyList<RuntimeInstance>>> read) : IRuntimeRegistry
{
    public Task<IReadOnlyList<RuntimeInstance>> ReadAsync(CancellationToken cancellationToken) => read(cancellationToken);
    public IDisposable Watch(Action changed) => files.Watch(directory, changed);
}
