using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Audio;

// One host capture across every instance; the last subscription releases the device before
// a new capture is allowed to start. Individual slow consumers are disconnected.
public sealed class SharedAudioCapture(IAudioCapture capture, string? deviceId = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly object gate = new();
    private readonly HashSet<IAudioConnection> clients = [];
    private CancellationTokenSource? stop;
    private Task running = Task.CompletedTask;
    private bool disposed, finishing;
    public async Task<IAsyncDisposable> AttachAsync(IAudioConnection connection, CancellationToken token)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            while (true)
            {
                Task? previous;
                lock (gate)
                {
                    previous = finishing && !running.IsCompleted ? running : null;
                    if (previous is null)
                    {
                        clients.Add(connection);
                        if (running.IsCompleted)
                        { stop?.Dispose(); stop = new(); finishing = false; running = CaptureAsync(stop.Token); }
                    }
                }
                if (previous is null) break;
                await previous.WaitAsync(token).ConfigureAwait(false);
            }
            return new Subscription(this, connection);
        }
        finally { serial.Release(); }
    }
    private async Task CaptureAsync(CancellationToken token)
    {
        await Task.Yield();
        try
        {
            token.ThrowIfCancellationRequested();
            await foreach (var frame in capture.CaptureAsync(deviceId, token).WithCancellation(token).ConfigureAwait(false))
            {
                IAudioConnection[] active; lock (gate) active = clients.ToArray();
                foreach (var client in active)
                    if (!client.TryWrite(frame)) { lock (gate) clients.Remove(client); await client.DisposeAsync().ConfigureAwait(false); }
                lock (gate) if (clients.Count == 0) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { /* Closing the clients reports capture failure as EOF, never endless silence. */ }
        finally
        {
            IAudioConnection[] active; lock (gate) { finishing = true; active = clients.ToArray(); clients.Clear(); }
            foreach (var client in active) await client.DisposeAsync().ConfigureAwait(false);
        }
    }
    private async ValueTask DetachAsync(IAudioConnection connection)
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            bool last; lock (gate) { clients.Remove(connection); last = clients.Count == 0; }
            await connection.DisposeAsync().ConfigureAwait(false);
            if (last && stop is not null) { await stop.CancelAsync().ConfigureAwait(false); await running.ConfigureAwait(false); }
        }
        finally { serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try { disposed = true; if (stop is not null) await stop.CancelAsync().ConfigureAwait(false); await running.ConfigureAwait(false); }
        finally { serial.Release(); }
    }
    private sealed class Subscription(SharedAudioCapture owner, IAudioConnection connection) : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? owner.DetachAsync(connection) : ValueTask.CompletedTask;
    }
}
