using System.Net.NetworkInformation;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Windows.Forwards;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Windows;

/// <summary>
/// The host side of port forwarding: the exact netsh command lines, and the reconciliation matrix that
/// decides which rules are added, updated and deleted. Reconciliation is the part that can destroy
/// working state, so every branch of it is pinned here.
/// </summary>
public sealed class NetshPortForwardManagerTests
{
    [Fact]
    public async Task Allocating_the_ssh_forward_adds_the_rule_the_vm_needs()
    {
        var world = new World();
        await world.AddVmAsync("work-vm");

        var port = await world.Manager.AllocateSshForwardAsync("work-vm", CancellationToken.None);

        Assert.Equal(2201, port);
        Assert.Equal("netsh.exe", world.Runner[0].FileName);
        Assert.Equal(
            [
                "interface", "portproxy", "add", "v4tov4",
                "listenaddress=0.0.0.0",
                "listenport=2201",
                "connectaddress=172.20.144.5",
                "connectport=22",
            ],
            world.Runner[0].Arguments);
    }

    [Fact]
    public async Task The_listen_address_is_configurable_for_a_multi_homed_host()
    {
        var world = new World(o => o.ListenAddress = "192.168.1.10");
        await world.AddVmAsync("work-vm");

        await world.Manager.AllocateSshForwardAsync("work-vm", CancellationToken.None);

        Assert.Contains("listenaddress=192.168.1.10", world.Runner[0].Arguments);
    }

    [Fact]
    public async Task The_port_is_durable_before_the_rule_exists()
    {
        // A crash between the two must leave an allocation with no rule (reconciliation repairs that),
        // never a live rule no allocation accounts for — which a later VM could be handed as well.
        var world = new World();
        await world.AddVmAsync("work-vm");
        world.Runner.Respond(new ProcessResult(1, string.Empty, "The requested operation requires elevation", false));

        await Assert.ThrowsAsync<PortForwardException>(
            () => world.Manager.AllocateSshForwardAsync("work-vm", CancellationToken.None));

        var vm = await world.Vms.GetAsync("work-vm", CancellationToken.None);
        Assert.Equal(2201, vm!.SshForwardPort);
    }

    [Fact]
    public async Task Releasing_the_ssh_forward_deletes_the_rule()
    {
        var world = new World();
        await world.AddVmAsync("work-vm");
        await world.Manager.AllocateSshForwardAsync("work-vm", CancellationToken.None);

        Assert.True(await world.Manager.ReleaseSshForwardAsync("work-vm", CancellationToken.None));

        Assert.Equal(
            ["interface", "portproxy", "delete", "v4tov4", "listenaddress=0.0.0.0", "listenport=2201"],
            world.Runner.Calls[^1].Arguments);
    }

    [Fact]
    public async Task A_host_forward_is_materialized_on_the_vms_port()
    {
        var world = new World();
        await world.AddVmAsync("work-vm");

        var result = await world.Manager.TryAddForwardAsync(
            "work-vm", 3000, ForwardTarget.Host, "vite dev", 16, CancellationToken.None);

        Assert.Equal(AddForwardStatus.Added, result.Status);
        Assert.Equal(2300, result.Forward!.PublicPort);
        Assert.Equal(
            [
                "interface", "portproxy", "add", "v4tov4",
                "listenaddress=0.0.0.0",
                "listenport=2300",
                "connectaddress=172.20.144.5",
                "connectport=3000",
            ],
            world.Runner[0].Arguments);
    }

    [Fact]
    public async Task A_client_forward_is_recorded_and_never_touches_the_host()
    {
        // Client forwards are opened on the user's PC by the extension; materializing them here would
        // be exactly the LAN exposure that target exists to avoid (plan §4.6).
        var world = new World();
        await world.AddVmAsync("work-vm");

        var result = await world.Manager.TryAddForwardAsync(
            "work-vm", 3000, ForwardTarget.Client, "vite dev", 16, CancellationToken.None);

        Assert.Equal(AddForwardStatus.Added, result.Status);
        Assert.Null(result.Forward!.PublicPort);
        Assert.Empty(world.Runner.Calls);
    }

    [Fact]
    public async Task A_forward_whose_rule_cannot_be_created_is_not_left_behind()
    {
        // A record with no rule would be a forward the user can see and nothing can reach, holding a
        // port for as long as the VM lives.
        var world = new World();
        await world.AddVmAsync("work-vm");
        world.Runner.Respond(new ProcessResult(1, string.Empty, "netsh: access denied", false));

        await Assert.ThrowsAsync<PortForwardException>(() => world.Manager.TryAddForwardAsync(
            "work-vm", 3000, ForwardTarget.Host, "vite dev", 16, CancellationToken.None));

        Assert.Empty(await world.Forwards.ListAsync("work-vm", CancellationToken.None));

        // …and the public port is free again for the next request.
        var retry = await world.Manager.TryAddForwardAsync(
            "work-vm", 3000, ForwardTarget.Host, "vite dev", 16, CancellationToken.None);
        Assert.Equal(2300, retry.Forward!.PublicPort);
    }

    [Fact]
    public async Task Removing_a_forward_deletes_its_rule()
    {
        var world = new World();
        await world.AddVmAsync("work-vm");
        var added = await world.Manager.TryAddForwardAsync(
            "work-vm", 3000, ForwardTarget.Host, "vite dev", 16, CancellationToken.None);

        Assert.True(await world.Manager.RemoveForwardAsync("work-vm", added.Forward!.Id, CancellationToken.None));

        Assert.Equal(
            ["interface", "portproxy", "delete", "v4tov4", "listenaddress=0.0.0.0", "listenport=2300"],
            world.Runner.Calls[^1].Arguments);
    }

    [Fact]
    public async Task A_netsh_that_refuses_to_delete_does_not_block_the_removal()
    {
        // Otherwise a VM could not be deleted because of a rule that may not even exist any more.
        var world = new World();
        await world.AddVmAsync("work-vm");
        var added = await world.Manager.TryAddForwardAsync(
            "work-vm", 3000, ForwardTarget.Host, "vite dev", 16, CancellationToken.None);
        world.Runner.Respond(new ProcessResult(1, string.Empty, "element not found", false));

        Assert.True(await world.Manager.RemoveForwardAsync("work-vm", added.Forward!.Id, CancellationToken.None));
        Assert.Empty(await world.Forwards.ListAsync("work-vm", CancellationToken.None));
    }

    // ── reconciliation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_adds_the_rule_for_a_stored_forward_that_has_none()
    {
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Runner.RespondStdout("Listen on ipv4:            Connect to ipv4:", string.Empty);

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, repaired);
        Assert.Equal(
            [
                "interface", "portproxy", "add", "v4tov4",
                "listenaddress=0.0.0.0", "listenport=2201",
                "connectaddress=172.20.144.5", "connectport=22",
            ],
            world.Runner[1].Arguments);
    }

    [Fact]
    public async Task Reconcile_leaves_a_correct_rule_alone()
    {
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.5    22");

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, repaired);
        Assert.Single(world.Runner.Calls);
    }

    [Fact]
    public async Task Reconcile_repoints_a_rule_after_the_vms_address_changed()
    {
        // Hyper-V's NAT DHCP hands out a new address across reboots; the plan calls this out as the
        // thing reconciliation exists for.
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Resolver.With("work-vm.fake.local", "172.20.144.99");
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.5    22");

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, repaired);
        Assert.Equal("delete", world.Runner[1].Arguments[2]);
        Assert.Equal("add", world.Runner[2].Arguments[2]);
        Assert.Contains("connectaddress=172.20.144.99", world.Runner[2].Arguments);
    }

    [Fact]
    public async Task Periodic_retry_repairs_all_host_forwards_after_guest_networking_arrives()
    {
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        foreach (var (port, vmPort) in new[] { (2300, 4096), (2301, 5178), (2302, 4700) })
        {
            await world.Forwards.AddAsync(
                new PortForward($"f{port}", "work-vm", vmPort, port, ForwardTarget.Host, "app", world.Clock.UtcNow),
                CancellationToken.None);
        }
        await world.Forwards.AddAsync(
            new PortForward("client", "work-vm", 8080, null, ForwardTarget.Client, "local", world.Clock.UtcNow),
            CancellationToken.None);
        string[] oldRules = [
            "0.0.0.0 2201 172.20.144.5 22", "0.0.0.0 2300 172.20.144.5 4096",
            "0.0.0.0 2301 172.20.144.5 5178", "0.0.0.0 2302 172.20.144.5 4700",
            "0.0.0.0 8080 10.0.0.9 80", "192.168.1.10 2301 10.0.0.9 80",
        ];

        // Service startup precedes guest DHCP: preserve all rules and allocations.
        world.Resolver.Addresses.Clear();
        world.Runner.RespondStdout(oldRules);
        Assert.Equal(0, await world.Manager.ReconcileAsync(CancellationToken.None));
        Assert.Single(world.Runner.Calls);
        Assert.Equal(1, world.Resolver.Reads);

        var service = new Constructd.Api.Hosting.ForwardReconciliationService(
            world.Manager, new ConstructdOptions(),
            NullLogger<Constructd.Api.Hosting.ForwardReconciliationService>.Instance);
        world.Resolver.With("work-vm.fake.local", "172.20.144.99");
        world.Runner.RespondStdout(oldRules);

        await service.TickAsync(CancellationToken.None);

        var adds = world.Runner.Calls.Where(call => call.Arguments[2] == "add").ToArray();
        Assert.Equal(4, adds.Length);
        Assert.All(adds, call => Assert.Contains("connectaddress=172.20.144.99", call.Arguments));
        Assert.Equal(new[] { "connectport=22", "connectport=4096", "connectport=5178", "connectport=4700" },
            adds.Select(call => call.Arguments.Last()));
        Assert.Equal(2, world.Resolver.Reads);
        Assert.Equal(4, (await world.Forwards.ListAsync("work-vm", CancellationToken.None)).Count);

        // The next pass sees the new address and does not disrupt established connections.
        var before = world.Runner.Calls.Count;
        world.Runner.RespondStdout(oldRules.Select(rule => rule.Replace("172.20.144.5", "172.20.144.99")).ToArray());
        await service.TickAsync(CancellationToken.None);
        Assert.Equal(before + 1, world.Runner.Calls.Count);
    }

    [Fact]
    public async Task Periodic_reconciliation_retries_after_a_transient_failure_without_logging_dependency_text()
    {
        var logs = new LogSink();
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        var service = new Constructd.Api.Hosting.ForwardReconciliationService(
            world.Manager, new ConstructdOptions(), logs.Logger<Constructd.Api.Hosting.ForwardReconciliationService>());
        world.Runner.Failure = new InvalidOperationException("SENTINEL-SECRET");
        await service.TickAsync(CancellationToken.None);
        Assert.DoesNotContain("SENTINEL-SECRET", logs.Text);
        Assert.Contains("reconcile failed", logs.Text);

        world.Runner.Failure = null;
        world.Runner.RespondStdout("0.0.0.0 2201 172.20.144.7 22");
        await service.TickAsync(CancellationToken.None);
        Assert.Contains("connectaddress=172.20.144.5", world.Runner.Calls.Last().Arguments);
    }

    [Fact]
    public async Task Removing_a_forward_during_reconciliation_cannot_leave_a_resurrected_rule()
    {
        var resolver = new BlockingResolver();
        var world = new World(resolverOverride: resolver);
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        await world.Forwards.AddAsync(
            new PortForward("app", "work-vm", 5178, 2301, ForwardTarget.Host, "T3", world.Clock.UtcNow),
            CancellationToken.None);
        world.Runner.RespondStdout("0.0.0.0 2201 172.20.144.7 22", "0.0.0.0 2301 172.20.144.7 5178");
        var reconcile = world.Manager.ReconcileAsync(CancellationToken.None);
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remove = world.Manager.RemoveForwardAsync("work-vm", "app", CancellationToken.None);
        try
        {
            Assert.False(remove.IsCompleted);
        }
        finally
        {
            resolver.Release.TrySetResult(System.Net.IPAddress.Parse("172.20.144.99"));
        }
        await reconcile.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await remove.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await world.Forwards.GetAsync("app", CancellationToken.None));
        Assert.Equal("delete", world.Runner.Calls.Last().Arguments[2]);
        Assert.Contains("listenport=2301", world.Runner.Calls.Last().Arguments);
    }

    private sealed class BlockingResolver : IHostAddressResolver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<System.Net.IPAddress?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<System.Net.IPAddress?> ResolveIPv4Async(string host, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Release.Task.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Reconcile_deletes_a_rule_in_our_range_that_nothing_accounts_for()
    {
        // A VM removed while the service was down leaves a rule pointing at nothing.
        var world = new World();
        world.Runner.RespondStdout("0.0.0.0    2250    172.20.144.7    22");

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, repaired);
        Assert.Equal(
            ["interface", "portproxy", "delete", "v4tov4", "listenaddress=0.0.0.0", "listenport=2250"],
            world.Runner[1].Arguments);
    }

    [Fact]
    public async Task Reconcile_never_touches_a_rule_outside_the_configured_ranges()
    {
        // The host may have port proxies that are none of this service's business.
        var world = new World();
        world.Runner.RespondStdout("0.0.0.0    8080    10.0.0.9    80");

        Assert.Equal(0, await world.Manager.ReconcileAsync(CancellationToken.None));
        Assert.Single(world.Runner.Calls);
    }

    [Fact]
    public async Task Reconcile_never_touches_a_rule_on_another_listen_address()
    {
        var world = new World(o => o.ListenAddress = "192.168.1.10");
        world.Runner.RespondStdout("0.0.0.0    2250    172.20.144.7    22");

        Assert.Equal(0, await world.Manager.ReconcileAsync(CancellationToken.None));
        Assert.Single(world.Runner.Calls);
    }

    [Fact]
    public async Task Reconcile_keeps_a_live_rule_when_the_vm_cannot_be_resolved_right_now()
    {
        // A DNS blip must not delete a working forward — that would take a running VM off the network
        // and there is nothing to re-create it until the next pass.
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Resolver.Addresses.Clear();
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.5    22");

        Assert.Equal(0, await world.Manager.ReconcileAsync(CancellationToken.None));
        Assert.Single(world.Runner.Calls);
    }

    [Fact]
    public async Task Reconcile_reserves_the_allocated_ports_so_a_new_vm_cannot_be_handed_one()
    {
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        await world.AddVmAsync("other-vm");
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.5    22");

        await world.Manager.ReconcileAsync(CancellationToken.None);
        var port = await world.Manager.AllocateSshForwardAsync("other-vm", CancellationToken.None);

        Assert.Equal(2202, port);
    }

    [Fact]
    public async Task Reconcile_materializes_stored_host_forwards_too()
    {
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        await world.Forwards.AddAsync(
            new PortForward("f1", "work-vm", 3000, 2300, ForwardTarget.Host, "vite", DateTimeOffset.UtcNow),
            CancellationToken.None);
        await world.Forwards.AddAsync(
            new PortForward("f2", "work-vm", 4000, null, ForwardTarget.Client, "api", DateTimeOffset.UtcNow),
            CancellationToken.None);
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.5    22");

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, repaired);
        Assert.Contains("listenport=2300", world.Runner[1].Arguments);
        Assert.Contains("connectport=3000", world.Runner[1].Arguments);
    }

    [Fact]
    public async Task Reconcile_leaves_a_stored_ssh_port_outside_the_current_ranges_completely_alone()
    {
        // The admin narrowed the range after the VM was created. Its rule is grandfathered: this
        // service touches nothing outside its configured ranges — which has to hold for the rules it
        // would ADD just as much as for the ones it would delete.
        var world = new World(o => o.SshForwardPorts = new PortRangeOptions(2250, 2299));
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.9    22");

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, repaired);
        Assert.Single(world.Runner.Calls);   // the `show`, and nothing else
    }

    [Fact]
    public async Task Reconcile_leaves_a_stored_host_forward_outside_the_current_ranges_completely_alone()
    {
        var world = new World(o => o.AppForwardPorts = new PortRangeOptions(2400, 2999));
        await world.AddVmAsync("work-vm");
        await world.Forwards.AddAsync(
            new PortForward("f1", "work-vm", 3000, 2300, ForwardTarget.Host, "vite", DateTimeOffset.UtcNow),
            CancellationToken.None);
        world.Runner.RespondStdout("0.0.0.0    2300    172.20.144.9    3000");

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, repaired);
        Assert.Single(world.Runner.Calls);
    }

    [Fact]
    public async Task Reconcile_checks_each_stored_port_against_its_own_range_not_the_union()
    {
        // The SSH range moved but the app range still covers the old port. Materializing it would put a
        // live rule on a port _appPorts considers free and will hand to the next host forward.
        var world = new World(o =>
        {
            o.SshForwardPorts = new PortRangeOptions(3000, 3099);
            o.AppForwardPorts = new PortRangeOptions(2201, 2999);
        });
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.9    22");

        var repaired = await world.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, repaired);
        Assert.Single(world.Runner.Calls);
    }

    [Fact]
    public async Task A_host_forward_port_that_now_falls_in_the_ssh_range_is_left_alone_too()
    {
        var world = new World(o =>
        {
            o.SshForwardPorts = new PortRangeOptions(2300, 2399);
            o.AppForwardPorts = new PortRangeOptions(2400, 2999);
        });
        await world.AddVmAsync("work-vm");
        await world.Forwards.AddAsync(
            new PortForward("f1", "work-vm", 3000, 2300, ForwardTarget.Host, "vite", DateTimeOffset.UtcNow),
            CancellationToken.None);
        world.Runner.RespondStdout("0.0.0.0    2300    172.20.144.9    3000");

        Assert.Equal(0, await world.Manager.ReconcileAsync(CancellationToken.None));
        Assert.Single(world.Runner.Calls);
    }

    [Fact]
    public async Task Reconcile_does_not_report_a_rule_repaired_when_netsh_refused_to_delete_it()
    {
        // The sweep exists to make an unaccounted-for rule stop existing — it is inside our range and
        // exposed on the LAN. Counting a delete netsh refused would report the host as reconciled while
        // the rule is still live and forwarding.
        var world = new World();
        await world.AddVmAsync("work-vm");
        world.Runner
            .RespondStdout("0.0.0.0    2300    172.20.144.9    3000")   // show: nothing accounts for it
            .Respond(new ProcessResult(1, string.Empty, string.Empty, TimedOut: false));   // delete refuses

        var ex = await Assert.ThrowsAsync<PortForwardException>(
            () => world.Manager.ReconcileAsync(CancellationToken.None));

        Assert.Equal(2300, ex.PublicPort);
        Assert.Equal(2, world.Runner.Calls.Count);   // the show and the failed delete
    }

    [Fact]
    public async Task Reconcile_fails_rather_than_adding_on_top_of_a_stale_rule_it_could_not_delete()
    {
        // Re-pointing is delete+add, because netsh has no update. If the delete does not take, the add
        // would land on a rule still pointing at the VM's old address.
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Runner
            .RespondStdout("0.0.0.0    2201    172.20.144.99    22")    // stale connectaddress
            .Respond(new ProcessResult(1, string.Empty, string.Empty, TimedOut: false));   // delete refuses

        await Assert.ThrowsAsync<PortForwardException>(
            () => world.Manager.ReconcileAsync(CancellationToken.None));

        Assert.Equal(2, world.Runner.Calls.Count);   // no `add` was attempted
    }

    [Fact]
    public async Task Tearing_a_forward_down_stays_best_effort_when_netsh_refuses()
    {
        // The other half of the rule: a VM must still be removable when the delete fails, because the
        // rule may not even exist any more.
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Runner.Default = new ProcessResult(1, string.Empty, string.Empty, TimedOut: false);

        Assert.True(await world.Manager.ReleaseSshForwardAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public async Task A_grandfathered_port_is_not_handed_out_again_by_the_other_allocator()
    {
        // The half of the cross-range problem that leaving the rule alone does NOT solve: the SSH range
        // moved off 2201, so the live rule there is grandfathered — but 2201 now sits in the APP range.
        // Unless the app allocator is told the port is occupied, the next host forward is handed 2201
        // and its netsh rule silently replaces the running VM's SSH forward.
        var world = new World(o =>
        {
            o.SshForwardPorts = new PortRangeOptions(3000, 3099);
            o.AppForwardPorts = new PortRangeOptions(2201, 2999);
        });
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        world.Runner.RespondStdout("0.0.0.0    2201    172.20.144.9    22");

        await world.Manager.ReconcileAsync(CancellationToken.None);
        var added = await world.Manager.TryAddForwardAsync(
            "work-vm", 3000, ForwardTarget.Host, "vite", maxForwards: 5, CancellationToken.None);

        Assert.Equal(2202, added.Forward!.PublicPort);
    }

    [Theory]
    [InlineData(2201, 2299, 2250, 2999)]   // app starts inside ssh
    [InlineData(2201, 2299, 2100, 2250)]   // app ends inside ssh
    [InlineData(2201, 2299, 2201, 2299)]   // identical
    [InlineData(2250, 2260, 2201, 2999)]   // ssh entirely inside app
    public void Overlapping_port_ranges_are_refused_at_startup(int sshStart, int sshEnd, int appStart, int appEnd)
    {
        // The two allocators are independent, so an overlap means both can hand out the same public
        // port and the second netsh rule silently replaces the first.
        var ex = Assert.Throws<InvalidOperationException>(() => new World(o =>
        {
            o.SshForwardPorts = new PortRangeOptions(sshStart, sshEnd);
            o.AppForwardPorts = new PortRangeOptions(appStart, appEnd);
        }));

        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adjacent_port_ranges_are_fine()
    {
        var world = new World(o =>
        {
            o.SshForwardPorts = new PortRangeOptions(2201, 2299);
            o.AppForwardPorts = new PortRangeOptions(2300, 2999);
        });

        Assert.NotNull(world.Manager);
    }

    [Fact]
    public async Task No_netsh_output_ever_reaches_a_log_entry()
    {
        // netsh echoes what it was given, and this service does not repeat dependency text anywhere —
        // the log included.
        using var logs = new LogSink();
        var world = new World(logs: logs);
        await world.AddVmAsync("work-vm");

        world.Runner.Respond(new ProcessResult(1, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: false));
        await Assert.ThrowsAsync<PortForwardException>(
            () => world.Manager.AllocateSshForwardAsync("work-vm", CancellationToken.None));

        // The delete path logs a warning rather than throwing, and the read path throws.
        world.Runner.Respond(new ProcessResult(1, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: false));
        await world.Manager.ReleaseSshForwardAsync("work-vm", CancellationToken.None);

        world.Runner.Respond(new ProcessResult(1, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: false));
        await Assert.ThrowsAsync<PortForwardException>(
            () => world.Manager.ReconcileAsync(CancellationToken.None));

        Assert.DoesNotContain("SENTINEL-OUT", logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL-ERR", logs.Text, StringComparison.Ordinal);
        Assert.Contains("netsh", logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_netsh_that_cannot_be_read_fails_reconciliation_rather_than_reporting_success()
    {
        var world = new World();
        world.Runner.Respond(new ProcessResult(1, string.Empty, "netsh is broken", false));

        var ex = await Assert.ThrowsAsync<PortForwardException>(
            () => world.Manager.ReconcileAsync(CancellationToken.None));

        Assert.Equal("Could not read the host's port-proxy rules.", ex.Message);
    }

    // ── idle signal ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Connections_are_counted_across_the_ssh_forward_and_the_host_forwards()
    {
        var world = new World();
        await world.AddVmAsync("work-vm", sshForwardPort: 2201);
        await world.Forwards.AddAsync(
            new PortForward("f1", "work-vm", 3000, 2300, ForwardTarget.Host, "vite", DateTimeOffset.UtcNow),
            CancellationToken.None);

        world.TcpTable
            .Add(2201, TcpState.Established)    // an SSH session (or a client tunnel riding it)
            .Add(2300, TcpState.Established)    // somebody on the dev server
            .Add(2201, TcpState.TimeWait)       // a session that already ended
            .Add(2202, TcpState.Established);   // another VM's forward

        Assert.Equal(2, await world.Manager.CountActiveConnectionsAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public async Task A_vm_with_no_forwards_reports_no_connections_without_reading_the_table()
    {
        var world = new World();
        await world.AddVmAsync("work-vm");

        Assert.Equal(0, await world.Manager.CountActiveConnectionsAsync("work-vm", CancellationToken.None));
        Assert.Equal(0, world.TcpTable.Reads);
    }

    /// <summary>Everything one manager needs, wired to fakes.</summary>
    [Theory]
    [InlineData(ForwardTarget.Client)]
    [InlineData(ForwardTarget.Host)]
    public async Task Child_destination_is_atomic_and_host_argv_uses_child_address_and_connect_port(ForwardTarget target)
    {
        var world = new World(); await world.AddVmAsync("child");
        var request = new ForwardRequest("vm:parent", ForwardRelationship.Parent, "child", "parent", target, 3000, 8080, "child", 16);
        var destination = new ForwardDestination("child", "parent", "172.20.144.99", 8080, "vm:parent", ForwardRelationship.Parent, target == ForwardTarget.Host);
        var result = await world.Manager.TryAddDestinationForwardAsync(request, destination, default);
        Assert.Equal(destination, result.Forward!.Destination);
        Assert.Equal(destination, (await world.Manager.ListAsync("child", default))[0].Destination);
        if (target == ForwardTarget.Client) Assert.Empty(world.Runner.Calls);
        else
        {
            // A synthetic future-authority input, not a capability exposed by Hyper-V today.
            Assert.Equal(new[] { "interface", "portproxy", "add", "v4tov4", "listenaddress=0.0.0.0", "listenport=2300", "connectaddress=172.20.144.99", "connectport=8080" }, world.Runner[0].Arguments);
            await world.Manager.ReconcileAsync(default);
            Assert.Contains(world.Runner.Calls, c => c.Arguments.Contains("connectaddress=172.20.144.99"));
        }
    }
    [Fact]
    public async Task Child_host_destination_without_authority_is_refused_before_allocating_or_running_netsh()
    {
        var world = new World(); await world.AddVmAsync("child");
        var request = new ForwardRequest("vm:parent", ForwardRelationship.Parent, "child", null, ForwardTarget.Host, 80, 80, "", 16);
        var d = new ForwardDestination("child", null, "172.20.144.99", 80, "vm:parent", ForwardRelationship.Parent, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Manager.TryAddDestinationForwardAsync(request, d, default));
        Assert.Empty(world.Runner.Calls); Assert.Empty(await world.Manager.ListAsync(null, default));
    }

    private sealed class World
    {
        public World(Action<ConstructdOptions>? configure = null, LogSink? logs = null, IHostAddressResolver? resolverOverride = null)
        {
            var options = PlatformOptions.Create(configure);
            Clock = new MutableClock();
            Vms = new InMemoryVmRepository();
            Forwards = new InMemoryForwardStore();
            Driver = new FakeHypervisorDriver();
            Runner = new RecordingProcessRunner();
            Resolver = new StubHostAddressResolver().With("work-vm.fake.local", "172.20.144.5");
            TcpTable = new FakeTcpTableReader();

            Manager = new NetshPortForwardManager(
                Clock, Vms, Forwards, Driver, Runner, resolverOverride ?? Resolver, TcpTable, options,
                logs is null
                    ? NullLogger<NetshPortForwardManager>.Instance
                    : logs.Logger<NetshPortForwardManager>());
        }

        public MutableClock Clock { get; }

        public InMemoryVmRepository Vms { get; }

        public InMemoryForwardStore Forwards { get; }

        public FakeHypervisorDriver Driver { get; }

        public RecordingProcessRunner Runner { get; }

        public StubHostAddressResolver Resolver { get; }

        public FakeTcpTableReader TcpTable { get; }

        public NetshPortForwardManager Manager { get; }

        public async Task AddVmAsync(string name, int? sshForwardPort = null)
        {
            Driver.SetState(name, VmState.Running);
            Resolver.With($"{name}.fake.local", name == "work-vm" ? "172.20.144.5" : "172.20.144.6");

            await Vms.AddAsync(
                new Vm(name, "owner", 4, 8, 100, Clock.UtcNow, VmState.Running, sshForwardPort, null,
                    new IdlePolicy(120, IdleAction.Save), Vm.NoForwards),
                maxVms: 10,
                CancellationToken.None);
        }
    }
}
