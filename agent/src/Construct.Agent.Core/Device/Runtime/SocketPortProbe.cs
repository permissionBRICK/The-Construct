// Moved unchanged from companion/src/Construct.Companion.Host/Runtime/SocketPortProbe.cs (plan 11.1: kept); only the namespace differs.
using System.Net;
using System.Net.Sockets;
using Construct.Companion.Core.Abstractions;
namespace Construct.Agent.Device.Runtime;

public sealed class SocketPortProbe : IPortProbe
{
    public Task<bool> IsFreeAsync(int port, string bindHost, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Parse(bindHost), port);
        try { listener.Start(); return Task.FromResult(true); } catch (SocketException) { return Task.FromResult(false); } finally { listener.Stop(); }
    }
}
