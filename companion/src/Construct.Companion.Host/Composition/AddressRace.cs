using System.Net;
using System.Net.Sockets;
namespace Construct.Companion.Host.Composition;

// Connects to every address of a host at once and keeps the first that answers. .NET tries
// addresses one after another, so a name whose first records are unreachable (a domain PC's
// own link-local IPv6 entries, a Hyper-V adapter) would cost a full connect timeout per request.
public static class AddressRace
{
    public static async ValueTask<Stream> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        return await ConnectAsync(addresses, port, timeout, cancellationToken);
    }
    public static async ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        race.CancelAfter(timeout);
        var attempts = addresses.Select(address => AttemptAsync(address, port, race.Token)).ToList();
        Exception? first = null;
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts); attempts.Remove(finished);
            try
            {
                var socket = await finished;
                race.Cancel();
                foreach (var other in attempts) _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { first ??= e; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw first is OperationCanceledException ? new SocketException((int)SocketError.TimedOut) : first!;
    }
    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken); return socket; }
        catch { socket.Dispose(); throw; }
    }
}
