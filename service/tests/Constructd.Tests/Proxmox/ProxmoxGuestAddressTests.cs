using System.Net;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Proxmox;
using Constructd.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Proxmox;

public sealed class ProxmoxGuestAddressTests
{
    private const string Uuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Resources = """[{"type":"qemu","node":"pve1","name":"child","vmid":101},{"type":"qemu","node":"pve1","name":"sibling","vmid":102}]""";
    private static string Config(string name = "child", string uuid = Uuid) => JsonSerializer.Serialize(new
    { name, smbios1 = "uuid=" + uuid, net0 = "virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0", net1 = "e1000=00:11:22:33:44:55,bridge=vmbr1" });
    private const string Interfaces = """
        {"result":[{"name":"eth0","hardware-address":"aa:bb:cc:dd:ee:ff","ip-addresses":[
        {"ip-address":"10.2.3.4","ip-address-type":"ipv4"},{"ip-address":"fd00::2","ip-address-type":"ipv6"},
        {"ip-address":"127.0.0.1"},{"ip-address":"fe80::2"},{"ip-address":"169.254.2.3"},{"ip-address":"garbage"}]},
        {"name":"docker0","hardware-address":"aa:bb:cc:dd:ee:ff","ip-addresses":[{"ip-address":"172.17.0.1"}]},
        {"name":"eth1","hardware-address":"00:11:22:33:44:55","ip-addresses":[{"ip-address":"10.3.3.4"}]},
        {"name":"unknown","hardware-address":"99:99:99:99:99:99","ip-addresses":[{"ip-address":"10.2.3.99"}]}]}
        """;
    private const string Host = """
        [{"ifname":"vmbr0","addr_info":[{"local":"10.2.3.1","prefixlen":24},{"local":"fd00::1","prefixlen":64}]},
        {"ifname":"management","addr_info":[{"local":"192.0.2.1","prefixlen":24}]}]
        """;
    private const string Neighbors = """
        [{"dev":"vmbr0","dst":"10.2.3.4","lladdr":"aa:bb:cc:dd:ee:ff","state":["REACHABLE"]},
        {"dev":"management","dst":"192.0.2.2","lladdr":"aa:bb:cc:dd:ee:ff","state":["STALE"]}]
        """;
    private static async Task<(ProxmoxGuestAddressProvider Provider, RecordingProcessRunner Runner, MutableClock Clock)> Create(bool sibling = false,
        ILogger<ProxmoxGuestAddressProvider>? logger = null)
    {
        var vms = new InMemoryVmRepository(); var clock = new MutableClock(); var runner = new RecordingProcessRunner();
        var vm = new Vm("child", "alice", 1, 1, 4, clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [], Kind: VmKind.Child, Incarnation: Uuid);
        await vms.AddAsync(vm, 10, default);
        if (sibling) await vms.AddAsync(vm with { Name = "sibling" }, 10, default);
        await vms.AddAsync(vm with { Name = "absent", State = VmState.Absent }, 10, default);
        await vms.AddAsync(vm with { Name = "pending", Incarnation = null }, 10, default);
        var options = new ConstructdOptions(); options.Proxmox.Node = "pve1";
        return (new(runner, options, vms, clock, logger), runner, clock);
    }
    [Fact]
    public async Task Snapshot_binds_reports_to_host_adapters_and_never_verifies_guest_claims()
    {
        var (provider, runner, clock) = await Create();
        runner.RespondStdout(Resources).RespondStdout(Config()).RespondStdout(Interfaces).RespondStdout(Host).RespondStdout(Neighbors);
        var snapshot = await provider.CaptureAsync(default);
        var addresses = await snapshot.GetReportedAddressesAsync("CHILD", default);
        Assert.Equal(new[] { "10.2.3.4", "fd00::2", "10.3.3.4" }, addresses.Select(a => a.Address));
        Assert.All(addresses, a => { Assert.False(a.Verified); Assert.Equal(GuestAddressSource.GuestAgent, a.Source); Assert.Equal(clock.UtcNow, a.ObservedAt); });
        Assert.Equal("net1", addresses[2].AdapterId);
        Assert.All(await snapshot.GetAdaptersAsync("child", default), a => { Assert.Equal(Uuid, a.VmId); Assert.True(a.MacSpoofingEnabled); });
        Assert.Equal(2, (await snapshot.GetGuestSubnetsAsync(default)).Count);
        Assert.Contains(IPAddress.Parse("192.0.2.1"), await snapshot.GetHostAddressesAsync(default));
        Assert.Equal("vmbr0", Assert.Single(await snapshot.GetNeighborsAsync(default)).InterfaceAlias);
        Assert.Equal(5, runner.Calls.Count); // Captured reads never re-query.
        Assert.Equal("qm", runner.Calls[2].FileName);
        Assert.Equal(new[] { "agent", "101", "network-get-interfaces" }, runner.Calls[2].Arguments);
        Assert.Equal(new[] { "-j", "addr", "show" }, runner.Calls[3].Arguments);
        Assert.Equal(new[] { "-j", "neigh", "show" }, runner.Calls[4].Arguments);
    }
    [Fact]
    public async Task Replacement_incarnation_is_never_queried_but_sibling_is_preserved()
    {
        var (provider, runner, _) = await Create(sibling: true);
        runner.RespondStdout(Resources).RespondStdout(Config(uuid: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))
            .RespondStdout(Config("sibling")).RespondStdout(Interfaces).RespondStdout(Host).RespondStdout(Neighbors);
        var snapshot = await provider.CaptureAsync(default);
        Assert.Empty(await snapshot.GetReportedAddressesAsync("child", default)); Assert.Empty(await snapshot.GetAdaptersAsync("child", default));
        Assert.Equal(3, (await snapshot.GetReportedAddressesAsync("sibling", default)).Count);
        Assert.Equal(new[] { "agent", "102", "network-get-interfaces" }, Assert.Single(runner.Calls, c => c.FileName == "qm").Arguments);
    }
    [Theory]
    [InlineData(1, false, "secret-agent-output")]
    [InlineData(0, true, "secret-agent-output")]
    [InlineData(0, false, "invalid-secret-json")]
    public async Task Agent_failures_leave_adapters_and_host_facts_available_without_leaking_output(int exit, bool timeout, string output)
    {
        using var logs = new CapturedLogs(); using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var (provider, runner, _) = await Create(logger: factory.CreateLogger<ProxmoxGuestAddressProvider>());
        runner.RespondStdout(Resources).RespondStdout(Config()).Respond(new ProcessResult(exit, output, output, timeout))
            .RespondStdout(Host).RespondStdout(Neighbors);
        var snapshot = await provider.CaptureAsync(default);
        Assert.Empty(await snapshot.GetReportedAddressesAsync("child", default));
        Assert.Equal(2, (await snapshot.GetAdaptersAsync("child", default)).Count); Assert.NotEmpty(await snapshot.GetHostAddressesAsync(default));
        Assert.DoesNotContain(output, logs.AllText()); Assert.NotEmpty(logs.Entries);
    }
    [Fact]
    public async Task Unavailable_host_facts_do_not_discard_guest_reports()
    {
        var (provider, runner, _) = await Create();
        runner.RespondStdout(Resources).RespondStdout(Config()).RespondStdout(Interfaces).RespondStdout("not-json").RespondStdout("not-json");
        var snapshot = await provider.CaptureAsync(default);
        Assert.Equal(3, (await snapshot.GetReportedAddressesAsync("child", default)).Count);
        Assert.Empty(await snapshot.GetHostAddressesAsync(default)); Assert.Empty(await snapshot.GetGuestSubnetsAsync(default));
        Assert.Empty(await snapshot.GetNeighborsAsync(default));
    }
    [Fact]
    public async Task Cancellation_propagates()
    {
        var (provider, _, _) = await Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CaptureAsync(new CancellationToken(true)));
    }
}
