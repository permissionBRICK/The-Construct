using System.Net;
using System.Net.Sockets;

namespace Construct.Agent.Engines;

/// <summary>
/// Connects to an engine by name the way RFC 8305 ("Happy Eyeballs") does: the addresses of the family the resolver
/// lists first are tried at once, the other family's after <see cref="Stagger"/> (or as soon as the first family has
/// failed), and whichever connects first is used; the other attempt is cancelled and its socket disposed. An engine
/// listens on IPv4 only (its <c>ListenUrl</c>), so a host name that also has an AAAA record otherwise waits for the
/// refused IPv6 connect (on Windows about 2 s of SYN retries) before .NET tries IPv4.
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
        var family = addresses[0].AddressFamily;
        var first = addresses.Where(a => a.AddressFamily == family).ToArray();
        var other = addresses.Where(a => a.AddressFamily != family).ToArray();

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = new List<Task<Socket>> { TryInTurnAsync(first, endpoint.Port, connect, race.Token) };
        if (other.Length > 0)
        {
            await Task.WhenAny(attempts[0], Task.Delay(stagger, race.Token)).ConfigureAwait(false);
            if (!attempts[0].IsCompletedSuccessfully) attempts.Add(TryInTurnAsync(other, endpoint.Port, connect, race.Token));
        }

        Exception? failure = null;
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

    // One family's addresses, one after the other.
    private static async Task<Socket> TryInTurnAsync(IPAddress[] addresses, int port, Func<IPEndPoint, CancellationToken, Task<Socket>> connect, CancellationToken ct)
    {
        SocketException? last = null;
        foreach (var address in addresses)
        {
            try { return await connect(new IPEndPoint(address, port), ct).ConfigureAwait(false); }
            catch (SocketException ex) when (!ct.IsCancellationRequested) { last = ex; }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

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
