using System.Net;
using System.Net.NetworkInformation;
using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>
/// A host TCP table a test writes by hand, standing in for the <c>GetExtendedTcpTable</c> P/Invoke.
/// </summary>
public sealed class FakeTcpTableReader : ITcpTableReader
{
    /// <summary>The rows <see cref="Read"/> returns.</summary>
    public List<TcpConnectionInfo> Rows { get; } = [];

    /// <summary>How often the table was read (the idle scheduler asks once per VM per tick).</summary>
    public int Reads { get; private set; }

    /// <summary>Adds a row on <paramref name="localPort"/> of the host.</summary>
    public FakeTcpTableReader Add(int localPort, TcpState state, string remote = "10.0.0.5", int remotePort = 51000)
    {
        Rows.Add(new TcpConnectionInfo(
            IPAddress.Parse("192.168.1.10"),
            localPort,
            IPAddress.Parse(remote),
            remotePort,
            state,
            OwningProcessId: 4));
        return this;
    }

    public IReadOnlyList<TcpConnectionInfo> Read()
    {
        Reads++;
        return [.. Rows];
    }
}
