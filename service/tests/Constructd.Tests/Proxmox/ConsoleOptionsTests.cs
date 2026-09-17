using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Tests.Proxmox;

public sealed class ConsoleOptionsTests
{
    [Fact]
    public void Defaults_preserve_VMConnect_and_reserve_a_separate_console_range()
    {
        var connection = new ConsoleConnection { VmId = "id", Username = "user", Password = "secret",
            Domain = "host", CertificateFingerprint = "pin" };
        Assert.Equal("vmconnect", connection.Protocol);
        Assert.Null(connection.Host);
        Assert.Null(connection.Port);
        Assert.DoesNotContain("secret", connection.ToString());
        var options = new ConstructdOptions();
        Assert.Equal(5900, options.Proxmox.ConsolePorts.Start);
        Assert.Equal(5999, options.Proxmox.ConsolePorts.End);
        options.Proxmox.ValidateConsolePorts(options.SshForwardPorts, options.AppForwardPorts);
    }

    [Theory]
    [InlineData(0, 5900)]
    [InlineData(5900, 65536)]
    [InlineData(5901, 5900)]
    [InlineData(2200, 2201)]
    [InlineData(2299, 2300)]
    [InlineData(2999, 3000)]
    [InlineData(1, 65535)]
    public void Invalid_or_overlapping_console_ranges_are_rejected(int start, int end)
    {
        var options = new ConstructdOptions();
        options.Proxmox.ConsolePorts = new(start, end);
        Assert.Throws<InvalidOperationException>(() =>
            options.Proxmox.ValidateConsolePorts(options.SshForwardPorts, options.AppForwardPorts));
    }
}
