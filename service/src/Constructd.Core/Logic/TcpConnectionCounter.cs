using System.Net.NetworkInformation;
using Constructd.Core.Abstractions;

namespace Constructd.Core.Logic;

/// <summary>
/// Turns a host TCP table into the per-VM connection count the idle engine asks for (plan §4.7).
///
/// A VM's forwards are host-side listeners, so a live client shows up as an <see cref="TcpState.Established"/>
/// row whose <em>local</em> port is one of the VM's public ports: its SSH forward plus every
/// <see cref="Domain.ForwardTarget.Host"/> forward. Only established rows count — a listener with no
/// client (<see cref="TcpState.Listen"/>) and a connection being torn down (<c>TIME_WAIT</c>,
/// <c>CLOSE_WAIT</c>, …) are not somebody using the VM.
///
/// Pure, so the decision is testable without a Windows TCP table.
/// </summary>
public static class TcpConnectionCounter
{
    public static int CountEstablished(
        IReadOnlyList<TcpConnectionInfo> connections,
        IReadOnlyCollection<int> publicPorts)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(publicPorts);

        if (publicPorts.Count == 0)
        {
            return 0;
        }

        var ports = publicPorts as HashSet<int> ?? [.. publicPorts];

        var count = 0;
        foreach (var connection in connections)
        {
            if (connection.State == TcpState.Established && ports.Contains(connection.LocalPort))
            {
                count++;
            }
        }

        return count;
    }
}
