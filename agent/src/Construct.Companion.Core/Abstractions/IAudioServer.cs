namespace Construct.Companion.Core.Abstractions;

// Loopback listener and bounded output connections. TryWrite=false means a slow/broken peer;
// callers close it instead of buffering unbounded PCM or stalling other recordings.
public interface IAudioServerFactory
{
    Task<IAudioServer> ListenAsync(CancellationToken cancellationToken);
}
public interface IAudioServer : IAsyncDisposable
{
    int Port { get; }
    IAsyncEnumerable<IAudioConnection> AcceptAsync(CancellationToken cancellationToken);
}
public interface IAudioConnection : IAsyncDisposable
{
    Task Closed { get; }
    bool TryWrite(ReadOnlyMemory<byte> pcm);
}
public interface IPortProbe
{
    Task<bool> IsFreeAsync(int port, string bindHost, CancellationToken cancellationToken);
}
