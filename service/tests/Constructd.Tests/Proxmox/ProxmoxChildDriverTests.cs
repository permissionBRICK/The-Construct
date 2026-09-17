using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Proxmox;

namespace Constructd.Tests.Proxmox;

public sealed partial class ProxmoxChildDriverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pve-child-" + Guid.NewGuid().ToString("n"));
    private readonly RecordingProcessRunner runner = new();
    private readonly ConstructdOptions options = new();
    private readonly InMemoryVmRepository vms = new();
    private readonly MutableClock clock = new();
    private readonly Dictionary<string, object> config = new();
    private readonly ProxmoxChildVmPlatform driver;
    private static readonly ChildHardware Hardware = new(2, 512, 4, 2, true, SecureBootTemplate.MicrosoftWindows,
        true, [BootDevice.InstallMedia, BootDevice.AuxiliaryMedia, BootDevice.Disk, BootDevice.Network], true);
    private const string Resources = """[{"type":"qemu","node":"pve1","name":"child","vmid":101}]""";
    private string Install => Path.Combine(root, new string('a', 32) + ".iso");
    private string Auxiliary => Path.Combine(root, new string('b', 32) + ".iso");
    private string Marker => Path.Combine(root, "children", "child.json");
    public ProxmoxChildDriverTests()
    {
        Directory.CreateDirectory(root);
        options.DatabasePath = Path.Combine(root, "test.db"); options.Proxmox.Node = "pve1";
        options.HostAdmin.Media.RootDir = root;
        File.WriteAllText(Install, "iso"); File.WriteAllText(Auxiliary, "iso");
        driver = new(runner, new FakeHypervisorDriver(), options, vms, clock);
    }
    private static ProcessResult Ok(object value) => new(0, JsonSerializer.Serialize(value), "", false);
    private void QueryConfig() => runner.Respond(_ => Ok(config));
    private void NamedConfig() { runner.RespondStdout(Resources); QueryConfig(); }
    private async Task Create(ChildHardware? hardware = null, bool fail = false)
    {
        runner.RespondStdout("[]").RespondStdout("101").Respond(call =>
        {
            Assert.True(File.Exists(Marker));
            for (var i = 2; i < call.Arguments.Count; i += 2) config[call.Arguments[i][2..]] = call.Arguments[i + 1];
            config["scsi0"] = "local-lvm:vm-101-disk-1,discard=on,size=4G";
            config["efidisk0"] = "local-lvm:vm-101-disk-0,efitype=4m,pre-enrolled-keys=1,size=4M";
            if (config.ContainsKey("tpmstate0")) config["tpmstate0"] = "local-lvm:vm-101-disk-2,version=v2.0,size=4M";
            return fail ? new(1, "dependency-secret", "dependency-secret", false) : new(0, "", "", false);
        });
        if (!fail) QueryConfig();
        await driver.CreateOwnedAsync(new("child", hardware ?? Hardware, null, Install, Auxiliary, "ignored-contract-switch"), "job-operation", null, default);
    }
    [Fact]
    public async Task Create_pins_hardware_media_boot_order_identity_and_stays_off()
    {
        await Create();
        var call = runner.Calls[2]; Assert.Equal("qm", call.FileName);
        Assert.Equal(TimeSpan.FromMinutes(30), call.Timeout);
        Assert.Equal(new[] { "create", "101", "--name", "child", "--ostype", "win11", "--machine", "q35",
            "--bios", "ovmf", "--efidisk0", "local-lvm:1,efitype=4m,pre-enrolled-keys=1", "--scsihw", "virtio-scsi-single",
            "--scsi0", "local-lvm:4,discard=on", "--agent", "enabled=1", "--memory", "512", "--balloon", "0", "--cores", "2",
            "--sockets", "1", "--tablet", "1", "--tags", "construct-child", "--smbios1", config["smbios1"],
            "--description", config["description"], "--tpmstate0", "local-lvm:1,version=v2.0", "--ide2",
            "construct-media:iso/" + Path.GetFileName(Install) + ",media=cdrom", "--ide0",
            "construct-media:iso/" + Path.GetFileName(Auxiliary) + ",media=cdrom", "--net0", "virtio,bridge=vmbr0",
            "--boot", "order=ide2;ide0;scsi0;net0" }, call.Arguments);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] is "start" or "agent");
        NamedConfig(); var id = await driver.GetVmIdAsync("child", default); Assert.True(Guid.TryParse(id, out _));
        NamedConfig(); Assert.Equal("job-operation", await driver.GetCreationOperationAsync("child", default));
        Assert.Contains("template=microsoftWindows", config["description"].ToString());
    }
    [Fact]
    public async Task Linux_without_network_tpm_or_secure_boot_and_parent_metadata()
    {
        await vms.AddAsync(new("child", "alice", 2, 1, 4, clock.UtcNow, VmState.Off, null, null, IdlePolicy.Disabled, [], Kind: VmKind.Child, Parent: "parent"), 10, default);
        await Create(Hardware with { Tpm = false, SecureBoot = false, SecureBootTemplate = null, NetworkAttached = false });
        var argv = runner.Calls[2].Arguments;
        Assert.DoesNotContain("--net0", argv); Assert.DoesNotContain("--tpmstate0", argv);
        Assert.Contains("l26", argv); Assert.Contains("local-lvm:1,efitype=4m,pre-enrolled-keys=0", argv);
        Assert.Contains("order=ide2;ide0;scsi0", argv); Assert.Contains("parent=parent", config["description"].ToString());
    }
    [Theory]
    [InlineData(1, false)] [InlineData(2, true)]
    public async Task Unsupported_hardware_never_allocates(int generation, bool dynamic)
    {
        var h = Hardware with { Generation = generation, DynamicMemory = dynamic ? new(1, 1, 1) : null };
        Assert.Equal("unsupported-capability", (await Assert.ThrowsAsync<ChildValidationException>(() =>
            driver.CreateAsync(new("child", h, null, Install, null, "switch"), null, default))).Code);
        Assert.Empty(runner.Calls);
    }
    [Fact]
    public async Task Foreign_disk_placement_is_refused_before_allocation()
    {
        Assert.Equal("storage-placement-unavailable", (await Assert.ThrowsAsync<ChildValidationException>(() =>
            driver.CreateAsync(new("child", Hardware, "/outside/disk", Install, null, "switch"), null, default))).Code);
        Assert.Empty(runner.Calls);
        Assert.Equal(new("local-lvm:vm-child-disk-0", "local-lvm", "local-lvm"), await driver.ResolvePrimaryStorageAsync("child", default));
    }
    [Fact]
    public async Task Name_taken_is_coded_and_does_not_create_a_marker()
    {
        runner.RespondStdout(Resources);
        Assert.Equal("name-taken", (await Assert.ThrowsAsync<ChildValidationException>(() =>
            driver.CreateAsync(new("child", Hardware, null, Install, null, "switch"), null, default))).Code);
        Assert.False(File.Exists(Marker)); Assert.Single(runner.Calls);
    }
    [Fact]
    public async Task Partial_create_retains_operation_for_rollback_and_sanitizes_failure()
    {
        var error = await Assert.ThrowsAsync<ProxmoxOperationException>(() => Create(fail: true));
        Assert.DoesNotContain("dependency-secret", error.ToString()); Assert.Null(error.InnerException);
        NamedConfig(); Assert.Equal("job-operation", await driver.GetCreationOperationAsync("child", default));
        await Remove(VmState.Off);
        Assert.False(File.Exists(Marker));
    }
    private async Task Remove(VmState state)
    {
        NamedConfig(); runner.RespondStdout(state == VmState.Running ? "{\"status\":\"running\"}" : "{\"status\":\"stopped\"}");
        if (state == VmState.Running) runner.RespondStdout("");
        runner.RespondStdout("").RespondStdout("[]").RespondStdout("[]");
        await driver.RemoveAsync("child", null, default);
    }
    [Theory]
    [InlineData(VmState.Off)] [InlineData(VmState.Running)]
    public async Task Remove_stops_only_when_needed_and_destroys_owned_artifacts(VmState state)
    {
        await Create(); await Remove(state);
        Assert.Equal(state == VmState.Running, runner.Calls.Any(c => c.Arguments[0] == "stop"));
        Assert.Equal(new[] { "destroy", "101", "--purge", "1", "--destroy-unreferenced-disks", "1" },
            Assert.Single(runner.Calls, c => c.Arguments[0] == "destroy").Arguments);
        Assert.False(File.Exists(Marker));
        runner.RespondStdout("[]"); await driver.RemoveAsync("child", null, default);
    }
    [Theory]
    [InlineData("smbios1", "uuid=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "vm-incarnation-conflict")]
    [InlineData("tags", "construct", "artifact-ownership-unverified")]
    [InlineData("description", "foreign", "artifact-ownership-unverified")]
    [InlineData("scsi0", "local-lvm:vm-999-disk-0,size=4G", "artifact-ownership-unverified")]
    public async Task Foreign_incarnation_or_artifacts_are_never_destroyed(string key, string value, string code)
    {
        await Create(); config[key] = value; NamedConfig();
        Assert.Equal(code, (await Assert.ThrowsAsync<ChildValidationException>(() => driver.RemoveAsync("child", null, default))).Code);
        Assert.True(File.Exists(Marker)); Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] is "destroy" or "stop");
    }
    [Fact]
    public async Task Failed_destroy_keeps_journal_and_retry_cleans_only_known_orphans()
    {
        await Create(); NamedConfig(); runner.RespondStdout("{\"status\":\"stopped\"}").Respond(new ProcessResult(1, "secret", "secret", false));
        await Assert.ThrowsAsync<ProxmoxOperationException>(() => driver.RemoveAsync("child", null, default));
        Assert.True(File.Exists(Marker));
        runner.RespondStdout("[]").RespondStdout("""[{"volid":"local-lvm:vm-101-disk-1"}]""").RespondStdout("");
        await driver.RemoveAsync("child", null, default);
        Assert.Equal("pvesm", runner.Calls[^1].FileName);
        Assert.Equal(new[] { "free", "local-lvm:vm-101-disk-1" }, runner.Calls[^1].Arguments);
        Assert.False(File.Exists(Marker));
    }
    [Fact]
    public async Task Missing_vm_does_not_authorize_unknown_disk_cleanup()
    {
        await Assert.ThrowsAsync<ProxmoxOperationException>(() => Create(fail: true));
        runner.RespondStdout("[]").RespondStdout("""[{"volid":"local-lvm:vm-101-disk-9"}]""");
        Assert.Equal("artifact-ownership-unverified", (await Assert.ThrowsAsync<ChildValidationException>(() => driver.RemoveAsync("child", null, default))).Code);
        Assert.True(File.Exists(Marker)); Assert.DoesNotContain(runner.Calls, c => c.FileName == "pvesm");
    }
    [Theory]
    [InlineData("../other")] [InlineData("child; stop")]
    public async Task Invalid_names_never_launch(string name)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => driver.RemoveAsync(name, null, default));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => driver.GetVmIdAsync(name, default));
        Assert.Empty(runner.Calls);
    }
    [Fact]
    public async Task Lookup_failures_are_not_absence_and_duplicate_names_are_refused()
    {
        runner.RespondStdout("[]"); Assert.Null(await driver.GetVmIdAsync("child", default));
        runner.RespondStdout("secret"); var error = await Assert.ThrowsAsync<ProxmoxOperationException>(() => driver.GetVmIdAsync("child", default));
        Assert.DoesNotContain("secret", error.ToString());
        runner.RespondStdout(Resources[..^1] + "," + Resources[1..]);
        Assert.Equal("vm-identity-ambiguous", (await Assert.ThrowsAsync<ChildValidationException>(() => driver.GetVmIdAsync("child", default))).Code);
    }
    [Fact]
    public async Task Capabilities_match_the_supported_hardware_and_legacy_suspend()
    {
        var caps = await driver.GetCapabilitiesAsync(default);
        Assert.Equal("proxmox", caps.Backend); Assert.Equal([2], caps.Generations); Assert.Equal(2, caps.MaxOpticalDrives);
        Assert.Equal(CapabilityLevel.Supported, caps.SecureBoot); Assert.Equal(CapabilityLevel.Supported, caps.Tpm);
        Assert.False(caps.SecureBootTemplateLockedAfterTpmInit);
        Assert.Equal(CapabilityLevel.Unsupported, caps.DynamicMemory); Assert.Equal(CapabilityLevel.Unsupported, caps.MemoryOvercommit);
        Assert.Equal(caps.Legacy.Suspend, caps.Suspend == CapabilityLevel.Supported);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
