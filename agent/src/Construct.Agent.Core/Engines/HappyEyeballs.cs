using System.Net;
using System.Net.Sockets;

namespace Construct.Agent.Engines;

/// <summary>
/// Connects to an engine by name the way RFC 8305 ("Happy Eyeballs") does: the addresses of the family the resolver
/// lists first are tried at once, the other family's after <see cref="Stagger"/> (or as soon as the first family has
/// failed), and whichever connects first is used; the other attempt is cancelled and its socket disposed. This keeps
/// connections fast even when one address family is unreachable, filtered, or served by an older IPv4-only engine.
/// </summary>
public static class HappyEyeballs
{
    /// <summary>How long the first family has before the other starts (RFC 8305's recommended connection attempt delay).</summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    /// <summary>The <see cref="SocketsHttpHandler.ConnectCallback"/> of every engine connection.</summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct) =>
        ConnectAsync(context.DnsEndPoint, Dns.GetHostAddressesAsync, ConnectSocketAsync, Stagger, ct);

    /// <param name="resolve">The host's addresses in the resolver's order (tests pass their own).</param>
    /// <param name="connect">One TCP connect; it disposes its socket when it fails or is cancelled.</param>
    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, Task<Socket>> connect, TimeSpan stagger, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal] : await resolve(endpoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        var ordered = Interleave(addresses);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = new List<Task<Socket>>(ordered.Count);
        Exception? failure = null;
        var next = 0;

        while (next < ordered.Count)
        {
            var started = ConnectOneAsync(ordered[next++], endpoint.Port, connect, race.Token);
            attempts.Add(started);
            if (next >= ordered.Count) break;

            using var staggerStop = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
            var delay = Task.Delay(stagger, staggerStop.Token);
            while (true)
            {
                var done = await Task.WhenAny(attempts.Append<Task>(delay)).ConfigureAwait(false);
                if (done == delay) break;

                var attempt = (Task<Socket>)done;
                attempts.Remove(attempt);
                if (attempt.IsCompletedSuccessfully)
                {
                    await race.CancelAsync().ConfigureAwait(false);
                    foreach (var loser in attempts) _ = DisposeWhenDoneAsync(loser);
                    return new NetworkStream(attempt.Result, ownsSocket: true);
                }

                failure ??= attempt.Exception?.InnerException;
                if (attempt == started || attempts.Count == 0)
                {
                    await staggerStop.CancelAsync().ConfigureAwait(false);
                    break;
                }
            }
        }

        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(done);
            if (done.IsCompletedSuccessfully)
            {
                await race.CancelAsync().ConfigureAwait(false);
                foreach (var loser in attempts) _ = DisposeWhenDoneAsync(loser);
                return new NetworkStream(done.Result, ownsSocket: true);
            }

            failure ??= done.Exception?.InnerException;
        }

        ct.ThrowIfCancellationRequested();
        throw failure ?? new SocketException((int)SocketError.HostUnreachable);
    }

    // RFC 8305 address interleaving: alternate the resolver's first family with the other so stalled addresses in either
    // family do not prevent trying later addresses.
    private static List<IPAddress> Interleave(IPAddress[] addresses)
    {
        var family = addresses[0].AddressFamily;
        var first = addresses.Where(a => a.AddressFamily == family).ToArray();
        var other = addresses.Where(a => a.AddressFamily != family).ToArray();
        var ordered = new List<IPAddress>(addresses.Length);
        var max = Math.Max(first.Length, other.Length);
        for (var i = 0; i < max; i++)
        {
            if (i < first.Length) ordered.Add(first[i]);
            if (i < other.Length) ordered.Add(other[i]);
        }

        return ordered;
    }

    private static Task<Socket> ConnectOneAsync(IPAddress address, int port, Func<IPEndPoint, CancellationToken, Task<Socket>> connect, CancellationToken ct) =>
        connect(new IPEndPoint(address, port), ct);

    // The attempt that lost the race: its socket, should it still connect, is closed; its failure is observed.
    private static async Task DisposeWhenDoneAsync(Task<Socket> attempt)
    {
        try { (await attempt.ConfigureAwait(false)).Dispose(); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task<Socket> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
