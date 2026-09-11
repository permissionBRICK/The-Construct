using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Runtime;

public sealed class LoopbackAudioServerFactory : IAudioServerFactory
{
    public Task<IAudioServer> ListenAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IAudioServer>(new Server()); }
    private sealed class Server : IAudioServer
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public Server() { listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        public int Port { get; }
        public async IAsyncEnumerable<IAudioConnection> AcceptAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
                catch (SocketException) when (cancellationToken.IsCancellationRequested) { yield break; }
                yield return new Connection(client);
            }
        }
        public ValueTask DisposeAsync() { listener.Stop(); return ValueTask.CompletedTask; }
    }
    private sealed class Connection : IAudioConnection
    {
        private readonly TcpClient client;
        private readonly CancellationTokenSource stop = new();
        private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(8);
        private readonly Task run;
        public Connection(TcpClient client) { this.client = client; run = RunAsync(); }
        public Task Closed => run;
        public bool TryWrite(ReadOnlyMemory<byte> pcm) => !run.IsCompleted && frames.Writer.TryWrite(pcm.ToArray());
        private async Task RunAsync()
        {
            var read = ReadAsync(); var write = WriteAsync();
            try { await Task.WhenAny(read, write).ConfigureAwait(false); }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false); client.Dispose(); frames.Writer.TryComplete();
                try { await Task.WhenAll(read, write).ConfigureAwait(false); } catch (Exception e) when (e is OperationCanceledException or SocketException or System.IO.IOException or ObjectDisposedException) { }
            }
        }
        private async Task ReadAsync()
        { var buffer = new byte[1]; while (await client.GetStream().ReadAsync(buffer, stop.Token).ConfigureAwait(false) > 0) { } }
        private async Task WriteAsync()
        { await foreach (var frame in frames.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false)) await client.GetStream().WriteAsync(frame, stop.Token).ConfigureAwait(false); }
        public async ValueTask DisposeAsync() { await stop.CancelAsync().ConfigureAwait(false); client.Dispose(); await run.ConfigureAwait(false); }
    }
}
public sealed class SocketPortProbe : IPortProbe
{
    public Task<bool> IsFreeAsync(int port, string bindHost, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Parse(bindHost), port);
        try { listener.Start(); return Task.FromResult(true); } catch (SocketException) { return Task.FromResult(false); } finally { listener.Stop(); }
    }
}
