using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Proxmox;

namespace Constructd.Tests.Proxmox;

/// <summary>The node's capacity picture out of three <c>pvesh</c> answers, and "unavailable" otherwise.</summary>
public sealed class ProxmoxInventoryTests
{
    private const string Status = """{"cpuinfo":{"cpus":8,"model":"i7"},"memory":{"total":8000000000,"free":6000000000,"used":2000000000}}""";
    private const string Storages = """[{"storage":"local","type":"dir","active":1,"total":70000000000,"avail":60000000000},{"storage":"local-lvm","type":"lvmthin","active":1,"total":143000000000,"avail":143000000000},{"storage":"broken","active":0,"total":1,"avail":1}]""";
    private const string Guests = """[{"vmid":104,"name":"work-vm","status":"running","cpus":4,"maxmem":8589934592,"maxdisk":64424509440,"mem":1000000,"cpu":0.25,"uptime":120},{"vmid":105,"name":"idle","status":"stopped","cpus":2,"maxmem":2147483648,"maxdisk":10737418240}]""";

    [Fact]
    public async Task Reads_host_volumes_and_vms_from_the_node()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages).RespondStdout(Guests);
        var inventory = new ProxmoxInventory(runner, Options(), new SystemClock());

        var snapshot = await inventory.ReadAsync(CancellationToken.None);

        Assert.True(snapshot.Complete);
        Assert.Empty(snapshot.Problems);
        Assert.Equal(8, snapshot.Host.LogicalCpus);
        Assert.Equal(8000000000, snapshot.Host.TotalRamBytes);
        Assert.Equal(6000000000, snapshot.Host.FreeRamBytes);
        Assert.Equal(["local", "local-lvm"], snapshot.Host.Volumes.Select(v => v.Root));
        Assert.Equal(2, snapshot.Vms.Count);
        var running = snapshot.Vms[0];
        Assert.Equal("work-vm", running.Name);
        Assert.Equal("104", running.Id);
        Assert.Equal(VmState.Running, running.State);
        Assert.Equal(4, running.Cpus);
        Assert.Equal(8589934592, running.MemoryAssignedBytes);
        Assert.Equal("local-lvm:vm-104-disk-0", running.Disks[0].Path);
        Assert.Equal(64424509440, running.Disks[0].MaxBytes);
        Assert.Equal(25, running.CpuUsagePercent);
        Assert.Equal("local-lvm", running.ConfigVolume);
        Assert.Equal("local-lvm", running.Disks[0].Volume);
        Assert.Equal(VmState.Off, snapshot.Vms[1].State);
        Assert.Equal(0, snapshot.Vms[1].MemoryAssignedBytes);
        Assert.Equal(["get", "/nodes/pve1/status", "--output-format", "json"], runner[0].Arguments);
        Assert.Equal(["get", "/nodes/pve1/storage", "--output-format", "json"], runner[1].Arguments);
        Assert.Equal(["get", "/nodes/pve1/qemu", "--output-format", "json"], runner[2].Arguments);
    }

    [Fact]
    public async Task A_failed_query_is_reported_as_unavailable_not_thrown()
    {
        var runner = new RecordingProcessRunner().Respond(new ProcessResult(2, string.Empty, "down", TimedOut: false));
        var inventory = new ProxmoxInventory(runner, Options(), new SystemClock());

        var snapshot = await inventory.ReadAsync(CancellationToken.None);

        Assert.False(snapshot.Complete);
        Assert.Equal(["inventory-unavailable"], snapshot.Problems);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Disk_reservations_get_evidence_by_vm_name()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages).RespondStdout(Guests);
        var inventory = new ProxmoxInventory(runner, Options(), new SystemClock());
        var rows = new[]
        {
            new Reservation("r1", ReservationResource.Storage, "alice", "work-vm", "disk:local-lvm:vm-work-vm-disk-0", "local-lvm", 64424509440, ReservationPhase.Held, ReservationOrigin.Api, null, DateTimeOffset.UtcNow, null, null),
            new Reservation("r2", ReservationResource.Storage, "alice", "ghost", "disk:local-lvm:vm-ghost-disk-0", "local-lvm", 1, ReservationPhase.Held, ReservationOrigin.Api, null, DateTimeOffset.UtcNow, null, null),
            new Reservation("r3", ReservationResource.Ram, "alice", "work-vm", null, null, 1, ReservationPhase.Held, ReservationOrigin.Api, null, DateTimeOffset.UtcNow, null, null),
        };

        var snapshot = await inventory.ReadAsync(rows, CancellationToken.None);

        Assert.NotNull(snapshot.Artifacts);
        Assert.Equal(2, snapshot.Artifacts!.Count);
        var present = snapshot.Artifacts[0];
        Assert.Equal(ArtifactPresence.Present, present.Presence);
        Assert.Equal("local-lvm:vm-work-vm-disk-0", present.Path);
        Assert.Equal(64424509440, present.FileBytes);
        Assert.Equal("local-lvm", present.Volume);
        Assert.Equal(ArtifactPresence.Absent, snapshot.Artifacts[1].Presence);
    }

    [Fact]
    public void A_status_without_numbers_is_no_snapshot()
    {
        using var status = JsonDocument.Parse("""{"cpuinfo":{}}""");
        using var empty = JsonDocument.Parse("[]");
        Assert.Null(ProxmoxInventory.Parse(1, DateTimeOffset.UtcNow, status.RootElement, empty.RootElement, empty.RootElement, "local-lvm"));
    }

    private static ConstructdOptions Options() => new() { Backend = "proxmox", Proxmox = new ProxmoxOptions { Node = "pve1" } };

    private const string Child = """[{"vmid":101,"name":"child","tags":"construct-child","status":"running","cpus":2,"maxmem":2147483648,"maxdisk":4294967296}]""";
    private const string ChildConfig = """
        {"name":"child","tags":"construct-child","smbios1":"uuid=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        "description":"construct-child parent=primary uuid=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa operation=op",
        "scsi0":"local-lvm:vm-101-disk-2,size=4G","efidisk0":"local-lvm:vm-101-disk-0,size=4M,pre-enrolled-keys=1",
        "tpmstate0":"local-lvm:vm-101-disk-1,size=4M"}
        """;
    private static Reservation ChildDisk() => new("r", ReservationResource.Storage, "alice", "child", "disk:local-lvm:vm-child-disk-0",
        "local-lvm", 4L << 30, ReservationPhase.Held, ReservationOrigin.Api, null, DateTimeOffset.UtcNow, null, null);

    [Fact]
    public async Task Tagged_child_uses_UUID_and_actual_guest_disk_placement_without_inventing_thin_allocation()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages).RespondStdout(Child)
            .RespondStdout(ChildConfig).RespondStdout("{\"status\":\"running\"}");
        var snapshot = await new ProxmoxInventory(runner, Options(), new SystemClock()).ReadAsync([ChildDisk()], default);
        Assert.True(snapshot.Complete); var vm = Assert.Single(snapshot.Vms);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", vm.Id);
        Assert.Equal(3, vm.Disks.Count); Assert.Equal("local-lvm:vm-101-disk-2", vm.Disks[0].Path);
        Assert.Equal(4L << 30, vm.Disks[0].MaxBytes); Assert.Equal(0, vm.Disks[0].FileBytes);
        var evidence = Assert.Single(snapshot.Artifacts!); Assert.Equal(vm.Disks[0].Path, evidence.Path);
        Assert.Equal(ArtifactPresence.Present, evidence.Presence);
        var managed = new Vm("child", "alice", 2, 2, 4, DateTimeOffset.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [],
            Kind: VmKind.Child, Incarnation: vm.Id);
        Assert.True(Constructd.Core.Logic.CapacityMath.Matches(managed, vm));
        Assert.False(Constructd.Core.Logic.CapacityMath.Matches(managed with { Incarnation = Guid.NewGuid().ToString() }, vm));
        Assert.Equal(new[] { "get", "/nodes/pve1/qemu/101/config", "--output-format", "json" }, runner.Calls[3].Arguments);
    }
    [Fact]
    public async Task Saved_child_retains_UUID_and_reports_saved_state_evidence()
    {
        var config = ChildConfig.TrimEnd().TrimEnd('}') + ",\"vmstate\":\"local-lvm:vm-101-state-suspend\"}";
        var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages).RespondStdout(Child)
            .RespondStdout(config).RespondStdout("{\"status\":\"stopped\",\"lock\":\"suspended\"}")
            .RespondStdout("[{\"volid\":\"local-lvm:vm-101-state-suspend\",\"size\":1234}]");
        var snapshot = await new ProxmoxInventory(runner, Options(), new SystemClock()).ReadAsync(default);
        Assert.True(snapshot.Complete); var vm = Assert.Single(snapshot.Vms);
        Assert.Equal(VmState.Saved, vm.State); Assert.Equal(1234, vm.SavedStateBytes); Assert.Equal(0, vm.MemoryAssignedBytes);
    }
    [Fact]
    public async Task Child_config_identity_mismatch_is_incomplete_evidence()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages).RespondStdout(Child)
            .RespondStdout(ChildConfig.Replace("parent=primary uuid=aaaaaaaa", "parent=primary uuid=bbbbbbbb"));
        var snapshot = await new ProxmoxInventory(runner, Options(), new SystemClock()).ReadAsync(default);
        Assert.False(snapshot.Complete); Assert.Equal("inventory-unavailable", Assert.Single(snapshot.Problems));
    }
    [Fact]
    public async Task Construct_primary_identity_agrees_with_the_enabled_child_identity_provider()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages)
            .RespondStdout(Child.Replace("construct-child", "construct")).RespondStdout(ChildConfig.Replace("construct-child", "construct"));
        var snapshot = await new ProxmoxInventory(runner, Options(), new SystemClock()).ReadAsync(default);
        Assert.True(snapshot.Complete);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Assert.Single(snapshot.Vms).Id);
        Assert.Equal(4, runner.Calls.Count);
    }
    [Fact]
    public async Task A_missing_VM_with_a_cleanup_journal_does_not_prove_its_disks_absent()
    {
        var dir = Directory.CreateTempSubdirectory("child-inventory-");
        try
        {
            var options = Options(); options.DatabasePath = Path.Combine(dir.FullName, "constructd.db");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "children"));
            File.WriteAllText(Path.Combine(dir.FullName, "children", "child.json"), "{}");
            var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages).RespondStdout("[]");
            var snapshot = await new ProxmoxInventory(runner, options, new SystemClock()).ReadAsync([ChildDisk()], default);
            Assert.Equal(ArtifactPresence.Unknown, Assert.Single(snapshot.Artifacts!).Presence);
        }
        finally { dir.Delete(true); }
    }
}
