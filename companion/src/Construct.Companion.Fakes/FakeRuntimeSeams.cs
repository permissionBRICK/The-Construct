using System.Text.Json.Nodes;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
namespace Construct.Companion.Fakes;

public sealed class FakePortProbe : IPortProbe
{
    public HashSet<int> Busy { get; } = [];
    public List<(int Port, string Host)> Probes { get; } = [];
    public Task<bool> IsFreeAsync(int port, string bindHost, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Probes.Add((port, bindHost)); return Task.FromResult(!Busy.Contains(port)); }
}
public sealed class FakePortReservations : IPortReservations
{
    private readonly HashSet<int> ports = [];
    public IDisposable? TryReserve(int port)
    { lock (ports) return ports.Add(port) ? new Release(() => { lock (ports) ports.Remove(port); }) : null; }
    private sealed class Release(Action release) : IDisposable
    { private int disposed; public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) release(); } }
}
public sealed class FakeRuntimeRegistry : IRuntimeRegistry
{
    public IReadOnlyList<RuntimeInstance> Instances { get; set; } = [];
    private event Action? Changed;
    public Exception? Error { get; set; }
    public Task<IReadOnlyList<RuntimeInstance>> ReadAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); if (Error is not null) throw Error; return Task.FromResult(Instances); }
    public IDisposable Watch(Action changed) { Changed += changed; return new Watcher(() => Changed -= changed); }
    public void Signal() => Changed?.Invoke();
    private sealed class Watcher(Action close) : IDisposable { public void Dispose() => close(); }
}
public sealed class FakeRuntimeProbe : IRuntimeProbe
{
    public JsonObject State { get; set; } = new() { ["online"] = true, ["vmState"] = "running" };
    public int Calls { get; private set; }
    public Task<JsonObject> ProbeAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(State.Copy()); }
}
public sealed class FakeRuntimeProcesses : IRuntimeProcesses
{
    public List<Session> Sessions { get; } = [];
    public ISupervisedProcess Start(Func<CancellationToken, IRunningProcess> spawn, ProcessSupervisionOptions options,
        Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output = null)
    { var session = new Session(changed, output); Sessions.Add(session); return session; }
    public sealed class Session(Action<SupervisedProcessState> changed, Func<string, CancellationToken, Task>? output) : ISupervisedProcess
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SupervisedProcessState State { get; private set; } = new("up");
        public Task<bool> FirstAttempt => Task.FromResult(true);
        public Task Completion => done.Task;
        public bool Disposed { get; private set; }
        public void SetState(string state) { State = new(state); changed(State); if (state is "failed" or "stopped") done.TrySetResult(); }
        public Task EmitAsync(string chunk) => output?.Invoke(chunk, CancellationToken.None) ?? Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; SetState("stopped"); return ValueTask.CompletedTask; }
    }
}
public sealed class FakeAudioServerFactory : IAudioServerFactory
{
    public List<FakeAudioServer> Servers { get; } = [];
    public Task<IAudioServer> ListenAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); var server = new FakeAudioServer(30000 + Servers.Count); Servers.Add(server); return Task.FromResult<IAudioServer>(server); }
}
public sealed class FakeAudioServer(int port) : IAudioServer
{
    private readonly Channel<IAudioConnection> connections = Channel.CreateUnbounded<IAudioConnection>();
    public int Port => port;
    public bool Disposed { get; private set; }
    public void Fail() => connections.Writer.TryComplete(new System.IO.IOException("Listener failed."));
    public FakeAudioConnection Connect() { var c = new FakeAudioConnection(); if (!connections.Writer.TryWrite(c)) throw new InvalidOperationException("Listener closed."); return c; }
    public IAsyncEnumerable<IAudioConnection> AcceptAsync(CancellationToken cancellationToken) => connections.Reader.ReadAllAsync(cancellationToken);
    public ValueTask DisposeAsync() { Disposed = true; connections.Writer.TryComplete(); while (connections.Reader.TryRead(out var c)) c.DisposeAsync().GetAwaiter().GetResult(); return ValueTask.CompletedTask; }
}
public sealed class FakeAudioConnection : IAudioConnection
{
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<byte[]> Frames { get; } = [];
    public bool Backpressure { get; set; }
    public Task Closed => closed.Task;
    public bool TryWrite(ReadOnlyMemory<byte> pcm) { lock (Frames) { if (Closed.IsCompleted || Backpressure) return false; Frames.Add(pcm.ToArray()); return true; } }
    public ValueTask DisposeAsync() { closed.TrySetResult(); return ValueTask.CompletedTask; }
}
public sealed class StreamingFakeAudioCapture : IAudioCapture
{
    private readonly Channel<byte[]> frames = Channel.CreateUnbounded<byte[]>();
    private int active, starts;
    public int Active => Volatile.Read(ref active);
    public int Starts => Volatile.Read(ref starts);
    public void Emit(params byte[] frame) => frames.Writer.TryWrite(frame);
    public void End() => frames.Writer.TryComplete();
    public Task<IReadOnlyList<AudioDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AudioDevice>>([]);
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(string? deviceId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref active); Interlocked.Increment(ref starts);
        try { await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return frame; }
        finally { Interlocked.Decrement(ref active); }
    }
}

// Pauses disposal so tests can place an arriving recording inside capture cleanup.
public sealed class PausedAudioConnection : IAudioConnection
{
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Closed => closed.Task;
    public Task Disposing => disposing.Task;
    public bool TryWrite(ReadOnlyMemory<byte> pcm) => !closed.Task.IsCompleted;
    public void FinishDisposal() => release.TrySetResult();
    public async ValueTask DisposeAsync() { disposing.TrySetResult(); await release.Task.ConfigureAwait(false); closed.TrySetResult(); }
}
public sealed class RestartableFakeAudioCapture : IAudioCapture
{
    public readonly List<Channel<byte[]>> Sessions = [];
    public Task<IReadOnlyList<AudioDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AudioDevice>>([]);
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(string? deviceId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<byte[]>(); lock (Sessions) Sessions.Add(channel);
        await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return frame;
    }
}
