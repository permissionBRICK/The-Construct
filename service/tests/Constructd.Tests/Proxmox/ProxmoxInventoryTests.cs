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
    public async Task Collection_timestamp_predates_all_queries()
    {
        var clock = new MutableClock();
        var started = clock.UtcNow;
        var runner = new RecordingProcessRunner().Respond(_ =>
        {
            clock.UtcNow = started.AddMinutes(1);
            return new(0, Status, "", false);
        }).RespondStdout(Storages).RespondStdout(Guests);
        var snapshot = await new ProxmoxInventory(runner, Options(), clock).ReadAsync(default);
        Assert.True(snapshot.Complete);
        Assert.Equal(started, snapshot.ObservedAt);
        Assert.Equal(started, snapshot.Host.ObservedAt);
    }

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
        Assert.Equal(2000000000, snapshot.Host.UsedRamBytes);
        Assert.Null(snapshot.Host.SwapTotalBytes);
        Assert.Null(snapshot.Host.SwapUsedBytes);
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
        Assert.Equal(1000000, running.MemoryDemandBytes);
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
        const string storageDefs = """[{"storage":"local-lvm","type":"lvmthin","vgname":"pve","thinpool":"data"}]""";
        var runner = new RecordingProcessRunner().RespondStdout(Status).RespondStdout(Storages).RespondStdout(Guests)
            .RespondStdout(storageDefs)
            .RespondStdout("  vm-104-disk-0|64424509440|25.00|data");
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
        Assert.Equal(16106127360, present.FileBytes);
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

    [Theory]
    [InlineData(4000000000, 1200000000)]
    [InlineData(0, 0)]
    public void Parses_optional_swap(long total, long used)
    {
        using var status = JsonDocument.Parse(Status[..^1] + $",\"swap\":{{\"total\":{total},\"used\":{used}}}}}");
        using var empty = JsonDocument.Parse("[]");
        var snapshot = ProxmoxInventory.Parse(1, DateTimeOffset.UtcNow, status.RootElement, empty.RootElement, empty.RootElement, "local-lvm");
        Assert.NotNull(snapshot);
        Assert.Equal(total, snapshot.Host.SwapTotalBytes);
        Assert.Equal(used, snapshot.Host.SwapUsedBytes);
    }
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
        Assert.Equal(3, vm.Disks.Count); Assert.Equal("local-lvm:vm-child-disk-0", vm.Disks[0].Path);
        Assert.Equal(4L << 30, vm.Disks[0].MaxBytes); Assert.Equal(0, vm.Disks[0].FileBytes);
        var evidence = Assert.Single(snapshot.Artifacts!); Assert.Equal(vm.Disks[0].Path, evidence.Path);
        Assert.Equal(ArtifactPresence.Present, evidence.Presence);
        var managed = new Vm("child", "alice", 2, 2, 4, DateTimeOffset.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [],
            Kind: VmKind.Child, Incarnation: vm.Id);
        Assert.True(Constructd.Core.Logic.CapacityMath.Matches(managed, vm));
        Assert.False(Constructd.Core.Logic.CapacityMath.Matches(managed with { Incarnation = Guid.NewGuid().ToString() }, vm));
        var accounted = Constructd.Core.Logic.CapacityMath.AccountedReservations(snapshot, [ChildDisk()], [managed]);
        Assert.Equal(4L << 30, accounted.Where(r => r.Artifact == ChildDisk().Artifact).Sum(r => r.Amount));
        Assert.Equal((4L << 30) + (8L << 20), accounted.Where(r => r.Artifact?.StartsWith("disk:") == true).Sum(r => r.Amount));
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
        Assert.Equal("local-lvm:vm-101-disk-2", vm.Disks[0].Path); // Native volume when there is no placement hold.
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
        Assert.Equal(5, runner.Calls.Count);
        Assert.Equal(["get", "/storage", "--output-format", "json"], runner.Calls[4].Arguments);
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

    [Fact]
    public async Task Reads_actual_zfs_disk_allocation_for_primaries_children_and_unmanaged_vms()
    {
        var options = Options();
        options.Proxmox.Storage = "vm-zfs";
        const string zfsStorages = """[{"storage":"vm-zfs","type":"zfspool","active":1,"total":437145808896,"avail":226300854272}]""";
        const string storageDefs = """[{"storage":"vm-zfs","type":"zfspool","pool":"rpool/crypt/vm","sparse":1}]""";
        const string zfsGuests = """
            [
              {"vmid":100,"name":"dev-a","tags":"construct","status":"running","cpus":8,"maxmem":12884901888,"maxdisk":214748364800,"mem":8000000000,"cpu":0.1,"uptime":1000},
              {"vmid":101,"name":"dev-b","tags":"construct","status":"running","cpus":8,"maxmem":12884901888,"maxdisk":85899345920,"mem":6000000000,"cpu":0.05,"uptime":500},
              {"vmid":102,"name":"child-a","tags":"construct-child","status":"stopped","cpus":2,"maxmem":4294967296,"maxdisk":85899345920},
              {"vmid":200,"name":"extra-vm","tags":"gpu","status":"running","cpus":8,"maxmem":10737418240,"maxdisk":68719476736}
            ]
            """;
        const string devAConfig = """{"name":"dev-a","tags":"construct","smbios1":"uuid=11111111-1111-1111-1111-111111111111","scsi0":"vm-zfs:vm-100-disk-0,discard=on,size=200G","ide2":"vm-zfs:vm-100-cloudinit,media=cdrom"}""";
        const string devBConfig = """{"name":"dev-b","tags":"construct","smbios1":"uuid=22222222-2222-2222-2222-222222222222","scsi0":"vm-zfs:vm-101-disk-0,discard=on,size=80G"}""";
        const string childAConfig = """{"name":"child-a","tags":"construct-child","smbios1":"uuid=33333333-3333-3333-3333-333333333333","description":"construct-child parent=dev-a uuid=33333333-3333-3333-3333-333333333333 operation=op","scsi0":"vm-zfs:vm-102-disk-1,size=80G","efidisk0":"vm-zfs:vm-102-disk-0,size=1M","tpmstate0":"vm-zfs:vm-102-disk-2,size=4M"}""";
        var zfsList = string.Join('\n', [
            "rpool/crypt/vm/vm-100-cloudinit\t110592",
            "rpool/crypt/vm/vm-100-disk-0\t135263346688",
            "rpool/crypt/vm/vm-101-cloudinit\t110592",
            "rpool/crypt/vm/vm-101-disk-0\t26463461376",
            "rpool/crypt/vm/vm-102-disk-0\t262144",
            "rpool/crypt/vm/vm-102-disk-1\t19672276992",
            "rpool/crypt/vm/vm-102-disk-2\t98304",
            "rpool/crypt/vm/vm-200-disk-0\t233472",
            "rpool/crypt/vm/vm-200-disk-1\t29377359872",
        ]);
        var childHold = new Reservation("rc", ReservationResource.Storage, "alice", "child-a", "disk:vm-zfs:vm-child-a-disk-0",
            "vm-zfs", 80L << 30, ReservationPhase.Held, ReservationOrigin.Api, null, DateTimeOffset.UtcNow, null, null);
        var runner = new RecordingProcessRunner()
            .RespondStdout(Status).RespondStdout(zfsStorages).RespondStdout(zfsGuests)
            .RespondStdout(devAConfig).RespondStdout(devBConfig)
            .RespondStdout(childAConfig).RespondStdout("{\"status\":\"stopped\"}")
            .RespondStdout(storageDefs).RespondStdout(zfsList);

        var snapshot = await new ProxmoxInventory(runner, options, new SystemClock()).ReadAsync([childHold], default);

        Assert.True(snapshot.Complete);
        var devA = snapshot.Vms.Single(v => v.Name == "dev-a");
        Assert.Single(devA.Disks);
        Assert.Equal(200L << 30, devA.Disks[0].MaxBytes);
        Assert.Equal(135263346688, devA.Disks[0].FileBytes);
        Assert.True(devA.Disks[0].AllocationKnown);

        var devB = snapshot.Vms.Single(v => v.Name == "dev-b");
        Assert.Single(devB.Disks);
        Assert.Equal(80L << 30, devB.Disks[0].MaxBytes);
        Assert.Equal(26463461376, devB.Disks[0].FileBytes);
        Assert.True(devB.Disks[0].AllocationKnown);

        // Child keeps placement path for ledger deduplication while still resolving allocation via its native volume id.
        var childA = snapshot.Vms.Single(v => v.Name == "child-a");
        Assert.Equal("vm-zfs:vm-child-a-disk-0", childA.Disks[0].Path);
        Assert.Equal(19672276992, childA.Disks[0].FileBytes);
        Assert.Equal(262144, childA.Disks[1].FileBytes);
        Assert.Equal(98304, childA.Disks[2].FileBytes);
        Assert.All(childA.Disks, d => Assert.True(d.AllocationKnown));
        Assert.Equal(19672276992, Assert.Single(snapshot.Artifacts!).FileBytes);

        // Unmanaged VM with an EFI disk at disk-0 gets the sum of all its vm-200-disk-* volumes, not just disk-0.
        var extra = snapshot.Vms.Single(v => v.Name == "extra-vm");
        Assert.Equal(233472 + 29377359872, extra.Disks[0].FileBytes);
        Assert.True(extra.Disks[0].AllocationKnown);

        var zfsCall = runner.Calls.Last();
        Assert.Equal("zfs", zfsCall.FileName);
        Assert.Equal(["list", "-Hp", "-o", "name,usedbydataset", "-t", "volume", "-r", "rpool/crypt/vm"], zfsCall.Arguments);
    }

    [Fact]
    public async Task Reads_actual_file_storage_allocation_with_stat_blocks()
    {
        var options = Options();
        options.Proxmox.Storage = "local";
        const string storageDefs = """[{"storage":"local","type":"dir","path":"/var/lib/vz"}]""";
        const string dirGuest = """[{"vmid":110,"name":"file-vm","tags":"construct","status":"running","cpus":2,"maxmem":4294967296,"maxdisk":107374182400}]""";
        const string dirConfig = """{"name":"file-vm","tags":"construct","smbios1":"uuid=44444444-4444-4444-4444-444444444444","scsi0":"local:110/vm-110-disk-0.qcow2,size=100G"}""";
        var runner = new RecordingProcessRunner()
            .RespondStdout(Status).RespondStdout(Storages).RespondStdout(dirGuest)
            .RespondStdout(dirConfig)
            .RespondStdout(storageDefs)
            .RespondStdout("20971520|/var/lib/vz/images/110/vm-110-disk-0.qcow2");

        var snapshot = await new ProxmoxInventory(runner, options, new SystemClock()).ReadAsync(default);

        Assert.True(snapshot.Complete);
        var vm = Assert.Single(snapshot.Vms);
        Assert.Equal(100L << 30, vm.Disks[0].MaxBytes);
        Assert.Equal(20971520L * 512L, vm.Disks[0].FileBytes);
        Assert.True(vm.Disks[0].AllocationKnown);
        var statCall = runner.Calls.Last();
        Assert.Equal("stat", statCall.FileName);
        Assert.Equal(["-c", "%b|%n", "/var/lib/vz/images/110/vm-110-disk-0.qcow2"], statCall.Arguments);
    }

    [Fact]
    public async Task Thick_lvm_reports_full_virtual_size_without_querying_lvs()
    {
        const string thickStorages = """[{"storage":"thick-vg","type":"lvm","active":1,"total":500000000000,"avail":400000000000}]""";
        const string storageDefs = """[{"storage":"thick-vg","type":"lvm","vgname":"vg0"}]""";
        var options = Options();
        options.Proxmox.Storage = "thick-vg";
        var runner = new RecordingProcessRunner()
            .RespondStdout(Status).RespondStdout(thickStorages).RespondStdout(Guests)
            .RespondStdout(storageDefs);

        var snapshot = await new ProxmoxInventory(runner, options, new SystemClock()).ReadAsync(default);

        Assert.True(snapshot.Complete);
        Assert.Equal(64424509440, snapshot.Vms[0].Disks[0].FileBytes);
        Assert.True(snapshot.Vms[0].Disks[0].AllocationKnown);
        Assert.DoesNotContain(runner.Calls, c => c.FileName == "lvs");
    }

    [Fact]
    public async Task Failed_allocation_query_keeps_inventory_complete_and_marks_allocation_unknown()
    {
        var options = Options();
        options.Proxmox.Storage = "vm-zfs";
        const string zfsStorages = """[{"storage":"vm-zfs","type":"zfspool","active":1,"total":437145808896,"avail":226300854272}]""";
        const string storageDefs = """[{"storage":"vm-zfs","type":"zfspool","pool":"rpool/crypt/vm"}]""";
        var runner = new RecordingProcessRunner()
            .RespondStdout(Status).RespondStdout(zfsStorages).RespondStdout(Guests)
            .RespondStdout(storageDefs)
            .Respond(new ProcessResult(1, string.Empty, "zfs failed", TimedOut: false));

        var snapshot = await new ProxmoxInventory(runner, options, new SystemClock()).ReadAsync(default);

        Assert.True(snapshot.Complete);
        Assert.All(snapshot.Vms, v => Assert.All(v.Disks, d =>
        {
            Assert.True(d.Readable);
            Assert.Equal(0, d.FileBytes);
            Assert.False(d.AllocationKnown);
        }));
    }

    [Fact]
    public async Task Storage_definitions_are_cached_between_polls_and_refreshed_after_ttl()
    {
        var clock = new MutableClock();
        var options = Options();
        options.Proxmox.Storage = "vm-zfs";
        const string zfsStorages = """[{"storage":"vm-zfs","type":"zfspool","active":1,"total":437145808896,"avail":226300854272}]""";
        const string storageDefs = """[{"storage":"vm-zfs","type":"zfspool","pool":"rpool/crypt/vm"}]""";
        var zfsList = "rpool/crypt/vm/vm-104-disk-0\t10737418240";
        var runner = new RecordingProcessRunner()
            .RespondStdout(Status).RespondStdout(zfsStorages).RespondStdout(Guests).RespondStdout(storageDefs).RespondStdout(zfsList)
            .RespondStdout(Status).RespondStdout(zfsStorages).RespondStdout(Guests).RespondStdout(zfsList)
            .RespondStdout(Status).RespondStdout(zfsStorages).RespondStdout(Guests).RespondStdout(storageDefs).RespondStdout(zfsList);
        var inventory = new ProxmoxInventory(runner, options, clock);

        var first = await inventory.ReadAsync(default);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = await inventory.ReadAsync(default);
        clock.Advance(TimeSpan.FromMinutes(6));
        var third = await inventory.ReadAsync(default);

        Assert.Equal(10737418240, first.Vms[0].Disks[0].FileBytes);
        Assert.Equal(10737418240, second.Vms[0].Disks[0].FileBytes);
        Assert.Equal(10737418240, third.Vms[0].Disks[0].FileBytes);
        Assert.Equal(2, runner.Calls.Count(c => c.Arguments.Count >= 2 && c.Arguments[1] == "/storage"));
    }
}
