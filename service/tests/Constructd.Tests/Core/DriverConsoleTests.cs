using Constructd.Core.Abstractions;

namespace Constructd.Tests.Core;

/// <summary>
/// The console capability is a kind, not a flag, because a client has to do something different for
/// each one: launch VMConnect, open a URL, or offer nothing (<c>docs/drivers.md</c> §3.1/§4). These
/// pin the mapping from the PowerShell contract's value onto that kind.
/// </summary>
public sealed class DriverConsoleTests
{
    [Theory]
    [InlineData("vmconnect")]
    [InlineData("VMConnect")]
    [InlineData("  vmconnect  ")]
    public void The_local_hyper_v_console_is_vmconnect(string reported)
    {
        var console = DriverConsole.Parse(reported);

        Assert.Equal(ConsoleKind.VmConnect, console.Kind);
        Assert.Null(console.Url);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void No_console_is_none(string? reported)
    {
        Assert.Equal(ConsoleKind.None, DriverConsole.Parse(reported).Kind);
    }

    [Fact]
    public void A_web_console_keeps_its_url()
    {
        // What a Proxmox-style backend reports (noVNC/xterm.js), per the mapping notes in docs/drivers.md §6.
        var console = DriverConsole.Parse("https://pve.example.local/?console=kvm&novnc=1&vmid=101");

        Assert.Equal(ConsoleKind.Url, console.Kind);
        Assert.Equal("https://pve.example.local/?console=kvm&novnc=1&vmid=101", console.Url);
    }

    [Theory]
    [InlineData("rdp://somewhere")]
    [InlineData("file:///etc/passwd")]
    [InlineData("something-new")]
    public void An_unrecognized_value_is_none_rather_than_a_url_a_client_would_open(string reported)
    {
        // A client that cannot tell what to open must be told there is nothing, not handed a scheme
        // it will hand to the shell.
        Assert.Equal(ConsoleKind.None, DriverConsole.Parse(reported).Kind);
    }

    [Fact]
    public void A_url_console_cannot_be_built_without_a_url()
    {
        Assert.Throws<ArgumentException>(() => DriverConsole.At("  "));
    }

    [Fact]
    public void Capabilities_compare_by_value()
    {
        // The fake and the tests both do `capabilities with { … }`; value equality is what makes that
        // predictable.
        var a = new DriverCapabilities(Checkpoints: true, Suspend: true, Console: DriverConsole.VmConnect);
        var b = new DriverCapabilities(Checkpoints: true, Suspend: true, Console: DriverConsole.Parse("vmconnect"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { Suspend = false });
    }
}
