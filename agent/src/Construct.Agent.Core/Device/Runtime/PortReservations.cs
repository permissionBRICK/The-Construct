// Moved unchanged from companion/src/Construct.Companion.Host/Runtime/PortReservations.cs (plan 11.1: kept); only the namespace differs.
using Construct.Companion.Core.Abstractions;
namespace Construct.Agent.Device.Runtime;

public sealed class PortReservations : IPortReservations
{
    private readonly HashSet<int> ports = [];
    public IDisposable? TryReserve(int port)
    {
        lock (ports) return ports.Add(port) ? new Reservation(this, port) : null;
    }
    private sealed class Reservation(PortReservations owner, int port) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) lock (owner.ports) owner.ports.Remove(port); }
    }
}
