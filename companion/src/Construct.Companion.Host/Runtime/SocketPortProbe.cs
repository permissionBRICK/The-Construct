using System.Net;
using System.Net.Sockets;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Runtime;

public sealed class SocketPortProbe : IPortProbe
{
    public Task<bool> IsFreeAsync(int port, string bindHost, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Parse(bindHost), port);
        try { listener.Start(); return Task.FromResult(true); } catch (SocketException) { return Task.FromResult(false); } finally { listener.Stop(); }
    }
}
