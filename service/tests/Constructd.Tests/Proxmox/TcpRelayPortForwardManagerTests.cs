using System.Net;
using System.Net.Sockets;
using System.Text;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Proxmox;
using Constructd.Windows.Forwards;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Proxmox;

/// <summary>
/// The in-process relay, driven with real loopback sockets: an SSH forward is allocated from the
/// range and recorded on the VM before it listens, bytes flow both ways to the address the driver
/// reports, the live connection count is the idle signal, and reconciliation restarts listeners
/// from the store after a restart.
/// </summary>
public sealed class TcpRelayPortForwardManagerTests
{
    [Fact]
    public async Task Ssh_forward_is_allocated_recorded_and_relays_to_the_guest()
    {
        using var guest = new EchoServer();
        var (manager, vms, _) = await ManagerAsync(guest.Port);

        var port = await manager.AllocateSshForwardAsync("work-vm", CancellationToken.None);

        Assert.InRange(port, 42101, 42109);
        Assert.Equal(port, (await vms.GetAsync("work-vm", CancellationToken.None))!.SshForwardPort);
        Assert.Equal(port, await manager.AllocateSshForwardAsync("work-vm", CancellationToken.None));

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("hello through the relay\n"));
            var reply = await ReadLineAsync(stream);
            Assert.Equal("hello through the relay", reply);
            await WaitForAsync(async () => await manager.CountActiveConnectionsAsync("work-vm", CancellationToken.None) == 1);
            Assert.Equal(0, await manager.CountActiveConnectionsAsync("other", CancellationToken.None));
        }

        await WaitForAsync(async () => await manager.CountActiveConnectionsAsync("work-vm", CancellationToken.None) == 0);

        Assert.True(await manager.ReleaseSshForwardAsync("work-vm", CancellationToken.None));
        Assert.Null((await vms.GetAsync("work-vm", CancellationToken.None))!.SshForwardPort);
        await AssertRefusedAsync(port);
        Assert.False(await manager.ReleaseSshForwardAsync("work-vm", CancellationToken.None));
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Host_forward_relays_to_the_vm_port_and_is_removed_with_the_vm()
    {
        using var guest = new EchoServer();
        var (manager, _, store) = await ManagerAsync(guest.Port);

        var added = await manager.TryAddForwardAsync("work-vm", guest.Port, ForwardTarget.Host, "web", 4, CancellationToken.None);
        Assert.Equal(AddForwardStatus.Added, added.Status);
        var publicPort = added.Forward!.PublicPort!.Value;
        Assert.InRange(publicPort, 42201, 42209);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, publicPort);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("app\n"));
            Assert.Equal("app", await ReadLineAsync(stream));
        }

        var clientOnly = await manager.TryAddForwardAsync("work-vm", 8080, ForwardTarget.Client, "panel", 4, CancellationToken.None);
        Assert.Null(clientOnly.Forward!.PublicPort);
        Assert.Equal(2, await store.CountByVmAsync("work-vm", CancellationToken.None));

        Assert.Equal(2, await manager.RemoveAllForwardsAsync("work-vm", CancellationToken.None));
        await AssertRefusedAsync(publicPort);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task The_cap_and_a_deleting_vm_are_enforced()
    {
        var (manager, vms, _) = await ManagerAsync(1);

        Assert.Equal(AddForwardStatus.Added, (await manager.TryAddForwardAsync("work-vm", 80, ForwardTarget.Client, "a", 1, CancellationToken.None)).Status);
        Assert.Equal(AddForwardStatus.LimitReached, (await manager.TryAddForwardAsync("work-vm", 81, ForwardTarget.Client, "b", 1, CancellationToken.None)).Status);

        var vm = (await vms.GetAsync("work-vm", CancellationToken.None))!;
        await vms.UpdateAsync(vm with { Deleting = true }, CancellationToken.None);
        Assert.Equal(AddForwardStatus.VmUnavailable, (await manager.TryAddForwardAsync("work-vm", 82, ForwardTarget.Client, "c", 9, CancellationToken.None)).Status);
        Assert.Equal(AddForwardStatus.VmUnavailable, (await manager.TryAddForwardAsync("ghost", 82, ForwardTarget.Client, "c", 9, CancellationToken.None)).Status);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Reconcile_restarts_listeners_from_the_store_after_a_restart()
    {
        using var guest = new EchoServer();
        var (first, vms, store) = await ManagerAsync(guest.Port);
        var sshPort = await first.AllocateSshForwardAsync("work-vm", CancellationToken.None);
        var app = await first.TryAddForwardAsync("work-vm", guest.Port, ForwardTarget.Host, "web", 4, CancellationToken.None);
        await first.DisposeAsync();
        await AssertRefusedAsync(sshPort);

        var second = NewManager(vms, store, guest.Port);
        Assert.Equal(2, await second.ReconcileAsync(CancellationToken.None));
        Assert.Equal(0, await second.ReconcileAsync(CancellationToken.None));
        Assert.Equal(sshPort, second.Listening["ssh:work-vm"]);
        Assert.Equal(app.Forward!.PublicPort, second.Listening[app.Forward.Id]);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, sshPort);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("back\n"));
            Assert.Equal("back", await ReadLineAsync(stream));
        }

        // The range remembers the allocation: the next VM gets a different port.
        await vms.AddAsync(NewVm("second-vm"), 10, CancellationToken.None);
        Assert.NotEqual(sshPort, await second.AllocateSshForwardAsync("second-vm", CancellationToken.None));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Overlapping_ranges_are_refused_at_construction()
    {
        var options = new ConstructdOptions
        {
            ListenAddress = "127.0.0.1",
            SshForwardPorts = new PortRangeOptions(42101, 42120),
            AppForwardPorts = new PortRangeOptions(42110, 42130),
        };
        var vms = new InMemoryVmRepository();
        Assert.Throws<InvalidOperationException>(() => new TcpRelayPortForwardManager(
            new SystemClock(), vms, new InMemoryForwardStore(), new StaticDriver(1), new DnsHostAddressResolver(),
            options, NullLogger<TcpRelayPortForwardManager>.Instance));
        await Task.CompletedTask;
    }

    private static async Task<(TcpRelayPortForwardManager Manager, InMemoryVmRepository Vms, InMemoryForwardStore Store)> ManagerAsync(int guestPort)
    {
        var vms = new InMemoryVmRepository();
        await vms.AddAsync(NewVm("work-vm"), 10, CancellationToken.None);
        var store = new InMemoryForwardStore();
        return (NewManager(vms, store, guestPort), vms, store);
    }

    private static TcpRelayPortForwardManager NewManager(InMemoryVmRepository vms, InMemoryForwardStore store, int guestPort)
    {
        var options = new ConstructdOptions
        {
            ListenAddress = "127.0.0.1",
            SshForwardPorts = new PortRangeOptions(42101, 42109),
            AppForwardPorts = new PortRangeOptions(42201, 42209),
        };
        return new TcpRelayPortForwardManager(new SystemClock(), vms, store, new StaticDriver(guestPort),
            new DnsHostAddressResolver(), options, NullLogger<TcpRelayPortForwardManager>.Instance);
    }

    private static Vm NewVm(string name) =>
        new(name, "alice", 2, 4, 40, DateTimeOffset.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [], TokenKind: VmTokenKind.Primary);

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new byte[256];
        var text = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (text.ToString().Contains('\n'))
            {
                break;
            }
        }

        return text.ToString().TrimEnd('\n', '\r');
    }

    private static async Task AssertRefusedAsync(int port)
    {
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("condition not met in time");
    }

    /// <summary>A driver whose every VM lives on loopback at one port — the relay's target.</summary>
    private sealed class StaticDriver(int port) : IHypervisorDriver
    {
        public DriverCapabilities Capabilities { get; } = new(false, true, DriverConsole.None);
        public Task CreateVmAsync(VmDescriptor descriptor, IProgress<string>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken) => Task.FromResult(VmState.Running);
        public Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Endpoint?>(new Endpoint("127.0.0.1", port));
        public Task<bool> WaitReachableAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Echoes each line back; stands in for the guest's sshd.</summary>
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public EchoServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (Exception) { break; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[256];
                            try
                            {
                                int read;
                                while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                                {
                                    await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                                }
                            }
                            catch (Exception) { }
                        }
                    });
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
