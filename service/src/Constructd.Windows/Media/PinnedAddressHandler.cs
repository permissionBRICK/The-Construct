using System.Net;
using System.Net.Sockets;
using Constructd.Core.Abstractions;
namespace Constructd.Windows.Media;

public interface IMediaDnsResolver { Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct); }
public sealed class MediaDnsResolver : IMediaDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => IPAddress.TryParse(host.Trim('[', ']'), out var ip)
        ? Task.FromResult(new[] { ip }) : Dns.GetHostAddressesAsync(host, ct);
}
public interface IMediaConnectionFactory
{
    Task<(Stream Stream, IPAddress Peer)> ConnectAsync(IPAddress address, int port, CancellationToken ct);
}
public sealed class MediaConnectionFactory : IMediaConnectionFactory
{
    public async Task<(Stream, IPAddress)> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(new IPEndPoint(address, port), ct); return (new NetworkStream(socket, ownsSocket: true), ((IPEndPoint)socket.RemoteEndPoint!).Address); }
        catch { socket.Dispose(); throw; }
    }
}
public static class PinnedAddressHandler
{
    public static SocketsHttpHandler Create(Uri url, IReadOnlyList<IPAddress> addresses, IUrlAdmissionPolicy policy, IMediaConnectionFactory connections)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, ct) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, url.IdnHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != url.Port)
                    throw new MediaException("url-refused");
                foreach (var address in addresses)
                {
                    if (!policy.Check(url, [address], true, true).Allowed) throw new MediaException("url-refused");
                    (Stream Stream, IPAddress Peer) connected;
                    try { connected = await connections.ConnectAsync(address, url.Port, ct); }
                    catch (SocketException) { continue; }
                    if (!connected.Peer.Equals(address) || !policy.Check(url, [connected.Peer], true, true).Allowed)
                    { await connected.Stream.DisposeAsync(); throw new MediaException("url-refused"); }
                    return connected.Stream;
                }
                throw new MediaException("media-transfer-failed");
            }
        };
    }
}
