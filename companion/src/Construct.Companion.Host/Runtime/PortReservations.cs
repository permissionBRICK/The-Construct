using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Runtime;

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
