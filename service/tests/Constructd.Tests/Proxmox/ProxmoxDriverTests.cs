using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Proxmox;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Proxmox;

/// <summary>
/// What the Proxmox driver actually runs: every operation is asserted as the exact <c>qm</c> /
/// <c>pvesh</c> argument vector, on a machine that has neither program. The state mapping pins the
/// one rule that matters most: <c>absent</c> comes from a successfully read resource list and
/// nothing else.
/// </summary>
public sealed class ProxmoxDriverTests
{
    private const string Resources =
        """[{"type":"qemu","vmid":104,"name":"work-vm","node":"pve1","status":"running"},{"type":"qemu","vmid":105,"name":"other","node":"pve1"},{"type":"lxc","vmid":200,"name":"ct","node":"pve1"}]""";

    private static readonly VmDescriptor Descriptor =
        new("work-vm", Cpu: 4, RamGb: 8, DiskGb: 60, IsoPath: "local:snippets/construct-work-vm-user.yaml",
            Nested: true, AutomaticCheckpoints: false);

    [Theory]
    [InlineData(true, "host")]
    [InlineData(false, "x86-64-v2-AES")]
    public async Task Nested_selects_cpu_for_create_and_pending_setting(bool nested, string model)
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner().RespondStdout("[]").RespondStdout("104")
            .RespondStdout("").RespondStdout("").RespondStdout("").RespondStdout(Resources).RespondStdout(""));
        await driver.CreateVmAsync(Descriptor with { Nested = nested }, null, default);
        var argv = runner[2].Arguments.ToArray();
        Assert.Equal(model, argv[Array.IndexOf(argv, "--cpu") + 1]);
        await driver.SetNestedAsync("work-vm", nested, default);
        Assert.Equal(["set", "104", "--cpu", model], runner[6].Arguments);
    }

    [Theory]
    [InlineData("kvm_intel", "Y", true)]
    [InlineData("kvm_amd", "1", true)]
    [InlineData("kvm_intel", "N", false)]
    [InlineData("kvm_amd", "0", false)]
    public void Nested_probe_reads_loaded_module_parameter(string module, string value, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "construct-kvm-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(ProxmoxNestedCapability.IsAvailable(root));
            var path = Path.Combine(root, module, "parameters");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "nested"), value + "\n");
            Assert.Equal(expected, ProxmoxNestedCapability.IsAvailable(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Network_configuration_updates_cloud_init_only_while_off(bool fixedAddress)
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner().RespondStdout(Resources)
            .RespondStdout("""{"status":"stopped"}""").RespondStdout("").RespondStdout(""));
        await driver.ConfigureNetworkAsync("work-vm", fixedAddress ? "203.0.113.50/24" : null,
            fixedAddress ? "203.0.113.1" : null, fixedAddress ? ["203.0.113.2", "203.0.113.3"] : null, default);
        Assert.Equal(fixedAddress
            ? new[] { "set", "104", "--ipconfig0", "ip=203.0.113.50/24,gw=203.0.113.1", "--nameserver", "203.0.113.2 203.0.113.3" }
            : new[] { "set", "104", "--ipconfig0", "ip=dhcp", "--delete", "nameserver" }, runner[2].Arguments);
        Assert.Equal(["cloudinit", "update", "104"], runner[3].Arguments);
    }

    [Theory]
    [InlineData("""{"status":"running"}""")]
    [InlineData("""{"status":"stopped","lock":"suspended"}""")]
    public async Task Network_configuration_refuses_running_and_saved_guests(string state)
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner().RespondStdout(Resources).RespondStdout(state));
        await Assert.ThrowsAsync<ProxmoxOperationException>(() => driver.ConfigureNetworkAsync("work-vm", null, null, null, default));
        Assert.All(runner.Calls, call => Assert.Equal("pvesh", call.FileName));
    }

    [Fact]
    public async Task Create_clones_the_image_seeds_cloud_init_resizes_and_starts()
    {
        var (driver, runner, seeds) = Driver(new RecordingProcessRunner()
            .RespondStdout("[]")            // resolve: no VM of that name yet
            .RespondStdout("\"104\"")       // nextid
            .RespondStdout("")              // qm create
            .RespondStdout("")              // qm disk resize
            .RespondStdout(""));            // qm start
        var progress = new List<string>();

        await driver.CreateVmAsync(Descriptor, new Progress<string>(progress.Add), CancellationToken.None);

        Assert.Equal("pvesh", runner[0].FileName);
        Assert.Equal(["get", "/cluster/resources", "--type", "vm", "--output-format", "json"], runner[0].Arguments);
        Assert.Equal(["get", "/cluster/nextid", "--output-format", "json"], runner[1].Arguments);

        var create = runner[2];
        Assert.Equal("qm", create.FileName);
        Assert.Equal(
        [
            "create", "104", "--name", "work-vm", "--cores", "4", "--sockets", "1", "--cpu", "host",
            "--memory", "8192", "--ostype", "l26", "--scsihw", "virtio-scsi-single",
            "--scsi0", "local-lvm:0,import-from=local:import/construct-ubuntu-noble-cloudimg-amd64.qcow2,discard=on",
            "--ide2", "local-lvm:cloudinit", "--net0", "virtio,bridge=vmbr0", "--boot", "order=scsi0",
            "--agent", "enabled=1", "--serial0", "socket", "--ipconfig0", "ip=dhcp",
            "--cicustom", "user=local:snippets/construct-work-vm-user.yaml", "--tags", "construct",
        ], create.Arguments);
        Assert.Equal(["disk", "resize", "104", "scsi0", "60G"], runner[3].Arguments);
        Assert.Equal(["start", "104"], runner[4].Arguments);
        Assert.Equal(5, runner.Calls.Count);
        Assert.Empty(seeds.Removed);
        Assert.Contains(progress, line => line.Contains("creating VM 104", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_refuses_a_name_that_already_exists_before_touching_qm()
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner().RespondStdout(Resources));

        var ex = await Assert.ThrowsAsync<ProxmoxOperationException>(
            () => driver.CreateVmAsync(Descriptor, null, CancellationToken.None));

        Assert.Equal("create-vm", ex.Operation);
        Assert.Single(runner.Calls);
        Assert.DoesNotContain("qm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_needs_a_snippet_seed_not_an_iso_path()
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner());

        await Assert.ThrowsAsync<ProxmoxOperationException>(() => driver.CreateVmAsync(
            Descriptor with { IsoPath = @"C:\isos\work-vm.iso" }, null, CancellationToken.None));
        await Assert.ThrowsAsync<ProxmoxOperationException>(() => driver.CreateVmAsync(
            Descriptor with { IsoPath = null }, null, CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_failed_qm_call_reports_its_stderr_as_progress_but_not_in_the_error()
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner()
            .RespondStdout("[]")
            .RespondStdout("104")
            .Respond(new ProcessResult(255, string.Empty, "storage 'local-lvm' does not exist\nsecond line", TimedOut: false)));
        var progress = new List<string>();

        var ex = await Assert.ThrowsAsync<ProxmoxOperationException>(
            () => driver.CreateVmAsync(Descriptor, new Progress<string>(progress.Add), CancellationToken.None));

        Assert.Equal("qm exited with 255", ex.Detail);
        Assert.DoesNotContain("local-lvm", ex.Message, StringComparison.Ordinal);
        Assert.Contains("qm: storage 'local-lvm' does not exist", progress);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task Remove_stops_a_running_vm_destroys_it_with_its_disks_and_drops_the_seed()
    {
        var (driver, runner, seeds) = Driver(new RecordingProcessRunner()
            .RespondStdout(Resources)
            .RespondStdout("""{"status":"running","qmpstatus":"running"}""")
            .RespondStdout("")
            .RespondStdout(""));

        await driver.RemoveVmAsync("work-vm", null, CancellationToken.None);

        Assert.Equal(["get", "/nodes/pve1/qemu/104/status/current", "--output-format", "json"], runner[1].Arguments);
        Assert.Equal(["stop", "104"], runner[2].Arguments);
        Assert.Equal(["destroy", "104", "--purge", "1", "--destroy-unreferenced-disks", "1", "--skiplock", "1"], runner[3].Arguments);
        Assert.Equal(["work-vm"], seeds.Removed);
    }

    [Fact]
    public async Task Remove_of_a_missing_vm_is_not_an_error()
    {
        var (driver, runner, seeds) = Driver(new RecordingProcessRunner().RespondStdout("[]"));
        var progress = new List<string>();

        await driver.RemoveVmAsync("gone", new Progress<string>(progress.Add), CancellationToken.None);

        Assert.Single(runner.Calls);
        Assert.Equal(["gone"], seeds.Removed);
        Assert.Contains(progress, line => line.Contains("nothing to remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Power_operations_map_onto_qm()
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner()
            .RespondStdout(Resources).RespondStdout("")                                                  // start
            .RespondStdout(Resources).RespondStdout("""{"status":"running"}""").RespondStdout("")      // stop
            .RespondStdout(Resources).RespondStdout("")                                                  // save
            .RespondStdout(Resources).RespondStdout("")                                                  // cpu
            .RespondStdout(Resources).RespondStdout(""));                                                // memory

        await driver.StartAsync("work-vm", CancellationToken.None);
        await driver.StopAsync("work-vm", CancellationToken.None);
        await driver.SaveAsync("work-vm", CancellationToken.None);
        await driver.SetCpuCountAsync("work-vm", 6, CancellationToken.None);
        await driver.SetMemoryAsync("work-vm", 12, CancellationToken.None);

        Assert.Equal(["start", "104"], runner[1].Arguments);
        Assert.Equal(["shutdown", "104", "--timeout", "120", "--forceStop", "1"], runner[4].Arguments);
        Assert.Equal(["suspend", "104", "--todisk", "1"], runner[6].Arguments);
        Assert.Equal(["set", "104", "--cores", "6"], runner[8].Arguments);
        Assert.Equal(["set", "104", "--memory", "12288"], runner[10].Arguments);
        Assert.All(runner.Calls.Where(c => c.FileName == "qm"), c => Assert.DoesNotContain("--output-format", c.Arguments));
    }

    [Fact]
    public async Task Stop_of_a_vm_that_is_off_or_saved_issues_no_shutdown()
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner()
            .RespondStdout(Resources).RespondStdout("""{"status":"stopped"}""")
            .RespondStdout(Resources).RespondStdout("""{"status":"stopped","lock":"suspended"}"""));

        await driver.StopAsync("work-vm", CancellationToken.None);
        await driver.StopAsync("work-vm", CancellationToken.None);

        Assert.Equal(4, runner.Calls.Count);
        Assert.All(runner.Calls, c => Assert.Equal("pvesh", c.FileName));
    }

    [Fact]
    public async Task State_is_absent_only_from_a_read_list_and_unknown_when_pvesh_fails()
    {
        var (driver, _, _) = Driver(new RecordingProcessRunner()
            .RespondStdout("[]")
            .Respond(new ProcessResult(2, string.Empty, "connection refused", TimedOut: false))
            .RespondStdout(Resources).RespondStdout("""{"status":"running","qmpstatus":"paused"}""")
            .RespondStdout(Resources).RespondStdout("""{"status":"stopped","lock":"suspended"}""")
            .RespondStdout(Resources).RespondStdout("""{"status":"stopped"}"""));

        Assert.Equal(VmState.Absent, await driver.GetStateAsync("work-vm", CancellationToken.None));
        Assert.Equal(VmState.Unknown, await driver.GetStateAsync("work-vm", CancellationToken.None));
        Assert.Equal(VmState.Paused, await driver.GetStateAsync("work-vm", CancellationToken.None));
        Assert.Equal(VmState.Saved, await driver.GetStateAsync("work-vm", CancellationToken.None));
        Assert.Equal(VmState.Off, await driver.GetStateAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public async Task A_vm_of_the_same_name_on_another_node_is_not_ours()
    {
        var elsewhere = """[{"type":"qemu","vmid":104,"name":"work-vm","node":"pve2"}]""";
        var (driver, _, _) = Driver(new RecordingProcessRunner().RespondStdout(elsewhere));

        Assert.Equal(VmState.Absent, await driver.GetStateAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public async Task Endpoint_is_the_guest_agents_first_real_ipv4_or_null_while_the_agent_is_silent()
    {
        var interfaces = """
            {"result":[
              {"name":"lo","ip-addresses":[{"ip-address":"127.0.0.1","ip-address-type":"ipv4"}]},
              {"name":"docker0","ip-addresses":[{"ip-address":"172.17.0.1","ip-address-type":"ipv4"}]},
              {"name":"eth0","ip-addresses":[{"ip-address":"fe80::1","ip-address-type":"ipv6"},{"ip-address":"203.0.113.201","ip-address-type":"ipv4"}]}
            ]}
            """;
        var (driver, runner, _) = Driver(new RecordingProcessRunner()
            .RespondStdout(Resources).RespondStdout(interfaces)
            .RespondStdout(Resources).Respond(new ProcessResult(2, string.Empty, "QEMU guest agent is not running", TimedOut: false))
            .RespondStdout("[]"));

        var endpoint = await driver.GetEndpointAsync("work-vm", CancellationToken.None);
        Assert.Equal(new Endpoint("203.0.113.201", 22), endpoint);
        Assert.Equal(["get", "/nodes/pve1/qemu/104/agent/network-get-interfaces", "--output-format", "json"], runner[1].Arguments);

        Assert.Null(await driver.GetEndpointAsync("work-vm", CancellationToken.None));
        Assert.Null(await driver.GetEndpointAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public void Guest_address_parsing_accepts_the_bare_array_too()
    {
        using var document = JsonDocument.Parse("""[{"name":"ens18","ip-addresses":[{"ip-address":"192.168.1.5","ip-address-type":"ipv4"}]}]""");
        Assert.Equal("192.168.1.5", ProxmoxDriver.ParseGuestAddress(document.RootElement));

        using var none = JsonDocument.Parse("""{"result":[{"name":"ens18","ip-addresses":[{"ip-address":"169.254.9.9","ip-address-type":"ipv4"}]}]}""");
        Assert.Null(ProxmoxDriver.ParseGuestAddress(none.RootElement));
    }

    [Fact]
    public void Capabilities_are_static_no_checkpoints_suspend_no_console()
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner());

        var caps = driver.Capabilities;

        Assert.False(caps.Checkpoints);
        Assert.True(caps.Suspend);
        Assert.Equal(ConsoleKind.None, caps.Console.Kind);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Detach_media_is_a_no_op()
    {
        var (driver, runner, _) = Driver(new RecordingProcessRunner());
        await driver.DetachInstallMediaAsync("work-vm", CancellationToken.None);
        Assert.Empty(runner.Calls);
    }

    private static (ProxmoxDriver Driver, RecordingProcessRunner Runner, RecordingSeeds Seeds) Driver(RecordingProcessRunner runner)
    {
        var options = new ConstructdOptions
        {
            Backend = "proxmox",
            Proxmox = new ProxmoxOptions { Node = "pve1" },
        };
        var seeds = new RecordingSeeds();
        return (new ProxmoxDriver(runner, options, seeds, NullLogger<ProxmoxDriver>.Instance), runner, seeds);
    }

    private sealed class RecordingSeeds : IProxmoxSeedFiles
    {
        public List<string> Removed { get; } = [];

        public void Remove(string vmName) => Removed.Add(vmName);
    }
}
