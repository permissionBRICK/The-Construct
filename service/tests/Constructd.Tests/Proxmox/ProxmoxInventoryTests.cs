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
}
