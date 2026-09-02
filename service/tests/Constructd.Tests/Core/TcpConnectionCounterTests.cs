using System.Net.NetworkInformation;
using Constructd.Core.Logic;
using Constructd.Fakes;

namespace Constructd.Tests.Core;

/// <summary>
/// The "no client connections" half of the idle check (plan §4.7). netsh portproxy keeps no per-rule
/// statistics, so the signal is established connections on the VM's public ports — and getting this
/// wrong either saves a VM somebody is using or keeps an idle one resident forever.
/// </summary>
public sealed class TcpConnectionCounterTests
{
    [Fact]
    public void Established_connections_on_the_vms_ports_are_counted()
    {
        var table = new FakeTcpTableReader()
            .Add(2201, TcpState.Established)
            .Add(2201, TcpState.Established)
            .Add(2301, TcpState.Established);

        Assert.Equal(3, TcpConnectionCounter.CountEstablished(table.Read(), [2201, 2301]));
    }

    [Fact]
    public void Another_vms_ports_are_not_counted()
    {
        var table = new FakeTcpTableReader()
            .Add(2201, TcpState.Established)
            .Add(2202, TcpState.Established);

        Assert.Equal(1, TcpConnectionCounter.CountEstablished(table.Read(), [2201]));
    }

    [Theory]
    [InlineData(TcpState.Listen)]
    [InlineData(TcpState.TimeWait)]
    [InlineData(TcpState.CloseWait)]
    [InlineData(TcpState.SynSent)]
    [InlineData(TcpState.FinWait1)]
    public void Only_established_rows_count_as_somebody_using_the_vm(TcpState state)
    {
        // The portproxy listener itself is always in Listen, and a closed session lingers in TimeWait
        // for minutes — counting either would mean a VM is never idle.
        var table = new FakeTcpTableReader().Add(2201, state);

        Assert.Equal(0, TcpConnectionCounter.CountEstablished(table.Read(), [2201]));
    }

    [Fact]
    public void A_vm_with_no_public_ports_has_no_connections()
    {
        var table = new FakeTcpTableReader().Add(2201, TcpState.Established);

        Assert.Equal(0, TcpConnectionCounter.CountEstablished(table.Read(), []));
    }

    [Fact]
    public void An_empty_table_counts_zero()
    {
        Assert.Equal(0, TcpConnectionCounter.CountEstablished([], [2201]));
    }
}
