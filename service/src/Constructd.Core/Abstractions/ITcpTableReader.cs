using System.Net;
using System.Net.NetworkInformation;

namespace Constructd.Core.Abstractions;

/// <summary>One row of the host's TCP connection table.</summary>
public sealed record TcpConnectionInfo(
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    TcpState State,
    int OwningProcessId);

/// <summary>
/// Reads the host's TCP connection table — the "no client connections" half of the idle check
/// (plan §4.7). <c>netsh interface portproxy</c> keeps no per-rule statistics, so the service counts
/// established connections on the VM's public ports instead (the plan's recorded approach, with an
/// in-process TCP proxy as the fallback if it ever proves flaky).
///
/// The Windows implementation is a <c>GetExtendedTcpTable</c> P/Invoke; it sits behind this interface
/// so the counting logic can be exercised without one.
/// </summary>
public interface ITcpTableReader
{
    /// <summary>A snapshot of the host's IPv4 TCP connections.</summary>
    IReadOnlyList<TcpConnectionInfo> Read();
}
