using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Capacity;

public class CapacityMathTests
{
    internal const long Gb = 1L << 30;
    internal static readonly DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
    internal static readonly CapacityConfig Config = HostAdminDefaults.Capacity with { Mode = CapacityMode.Enforce, RamHeadroomBytes = 4 * Gb, StorageHeadroomBytes = 0 };
    internal static Vm Vm(string name = "a", VmState state = VmState.Off, int ram = 8) => new(name, "alice", 2, ram, 20, Now, state, null, null, IdlePolicy.Disabled, [], Incarnation: name + "-id");
    internal static HypervisorVmInfo Actual(string name = "a", VmState state = VmState.Off, long assigned = 0, int ram = 8) =>
        new(name, name + "-id", state, state.ToString(), 2, 2, ram * Gb, assigned, false, null, [], null, "C:\\", true);
    internal static InventorySnapshot Inventory(long free = 28 * Gb, params HypervisorVmInfo[] vms) =>
        new(7, Now, new(8, 32 * Gb, free, [new("C:\\", 100 * Gb, 80 * Gb)], Now), vms, true, []);
    internal static Reservation Row(ReservationResource resource = ReservationResource.Ram, long amount = 8 * Gb,
        ReservationPhase phase = ReservationPhase.Pending, string vm = "a", string? artifact = null) =>
        new(Guid.NewGuid().ToString("n"), resource, "alice", vm, artifact, resource == ReservationResource.Storage ? "C:\\" : null,
            amount, phase, ReservationOrigin.Api, "op", Now, Now.AddMinutes(10), null);

    [Theory]
    [InlineData(27, 0, 0, 23)]
    [InlineData(12, 8, 0, 0)]
    [InlineData(8, 4, 0, 0)]
    [InlineData(6, 0, 16, 2)]
    public void ContractRamExamples(int free, int pending, int unmanaged, int expected)
    {
        var actual = unmanaged == 0 ? Array.Empty<HypervisorVmInfo>() : [Actual("external", VmState.Running, unmanaged * Gb, unmanaged)];
        var result = CapacityMath.Calculate(Inventory(free * Gb, actual), Config,
            pending == 0 ? [] : [Row(amount: pending * Gb)], [Vm()]);
        Assert.Equal(expected * Gb, result.RamAvailableBytes);
    }
    [Theory]
    [InlineData(VmState.Off, ReservationPhase.Held, 8, 0)]
    [InlineData(VmState.Unknown, ReservationPhase.Pending, 8, 0)]
    [InlineData(VmState.Unknown, ReservationPhase.Pending, 5, 3)]
    public void RestartAndPartialAllocationCannotSpendMemoryTwice(VmState state, ReservationPhase phase, int free, int assigned)
    {
        var result = CapacityMath.Calculate(Inventory(free * Gb, Actual(state: state, assigned: assigned * Gb)), Config, [Row(phase: phase)], [Vm()]);
        Assert.Equal(0, result.RamAvailableBytes);
    }
    [Fact]
    public void AllocatedMemoryIsNotSubtractedTwice()
    {
        var result = CapacityMath.Calculate(Inventory(20 * Gb, Actual(state: VmState.Running, assigned: 8 * Gb)), Config, [Row(phase: ReservationPhase.Held)], [Vm()]);
        Assert.Equal(16 * Gb, result.RamAvailableBytes);
    }
    [Fact]
    public void DynamicUnmanagedUsesMaximumButFixedIgnoresIt()
    {
        var actual = Actual("external", VmState.Running, 2 * Gb, 2) with { DynamicMemory = true, MemoryMaximumBytes = 24 * Gb };
        Assert.Equal(24 * Gb, CapacityMath.Calculate(Inventory(28 * Gb, actual), Config, [], []).RamUnmanagedBytes);
        Assert.Equal(2 * Gb, CapacityMath.Calculate(Inventory(28 * Gb, actual with { DynamicMemory = false }), Config, [], []).RamUnmanagedBytes);
    }
    [Fact]
    public void SharedDiskAndParentPathsCountGrowthOnce()
    {
        var disk = new HypervisorDiskInfo("C:\\disk.vhdx", 30 * Gb, 10 * Gb, null, "C:\\", true);
        var a = Actual() with { Disks = [disk] }; var b = Actual("b") with { Disks = [disk with { Path = "c:/DISK.vhdx" }] };
        var snapshot = CapacityMath.Calculate(Inventory(80 * Gb, a, b), Config,
            [Row(ReservationResource.Storage, 30 * Gb, artifact: "disk:C:\\disk.vhdx")], [Vm()]);
        Assert.Equal(20 * Gb, snapshot.Volumes[0].GrowthReservedBytes);
        Assert.Equal(60 * Gb, snapshot.Volumes[0].AvailableBytes);
    }
    [Fact]
    public void SavedStateKeepsFullMemoryPlusOverhead()
    {
        var snapshot = CapacityMath.Calculate(Inventory(28 * Gb, Actual(state: VmState.Saved) with { SavedStateBytes = Gb }), Config, [], []);
        Assert.Equal(7 * Gb + CapacityMath.SavedStateOverhead, snapshot.Volumes[0].GrowthReservedBytes);
    }
    [Fact]
    public void ExternalDiskGrowthDeltasRemainReservedWithoutReadableDisk()
    {
        var original = Row(ReservationResource.Storage, 10 * Gb, ReservationPhase.Held, artifact: "disk:C:\\grown.vhdx");
        var delta = Row(ReservationResource.Storage, 20 * Gb, ReservationPhase.Held, artifact: "disk:C:\\grown.vhdx");
        var snapshot = CapacityMath.Calculate(Inventory(), Config, [original, delta], [Vm()]);
        Assert.Equal(30 * Gb, snapshot.Volumes[0].GrowthReservedBytes);
        Assert.False(snapshot.Complete);
    }
    [Fact]
    public void ExternalDiskGrowthDoesNotInventAnAdditionalOwnerCharge()
    {
        var disk = new HypervisorDiskInfo("C:\\grown.vhdx", 30 * Gb, 10 * Gb, null, "C:\\", true);
        var rows = new[] { Row(ReservationResource.Storage, 10 * Gb, artifact: "disk:C:\\grown.vhdx"),
            Row(ReservationResource.Storage, 20 * Gb, artifact: "disk:C:\\grown.vhdx") };
        var snapshot = CapacityMath.Calculate(Inventory(28 * Gb, Actual() with { Disks = [disk] }), Config, rows, [Vm()]);
        Assert.Equal(30 * Gb, snapshot.Reservations.Sum(r => r.Amount));
        Assert.Equal(20 * Gb, snapshot.Volumes[0].GrowthReservedBytes);
    }
    [Fact]
    public void ObservedUnreservedConsumptionBindsCapacityWithoutFabricatedLedgerRows()
    {
        var snapshot = CapacityMath.Calculate(Inventory(20 * Gb, Actual(state: VmState.Running, assigned: 8 * Gb)), Config, [], [Vm()]);
        Assert.Empty(snapshot.Reservations); Assert.Equal(8 * Gb, snapshot.RamReservedBytes); Assert.Equal(2, snapshot.CpuActive);
        Assert.True(snapshot.Volumes[0].GrowthReservedBytes > 0);
    }
    [Theory]
    [InlineData(ReservationResource.Ram)] [InlineData(ReservationResource.Cpu)]
    public void RuntimeReservationRequiresVmName(ReservationResource resource) => Assert.Throws<ArgumentException>(() =>
        ReservationRules.Validate(new("alice", null, "op", [new(resource, 1, null, null)], TimeSpan.FromMinutes(10))));
    [Theory]
    [InlineData(VmState.Running)] [InlineData(VmState.Off)] [InlineData(VmState.Saved)] [InlineData(VmState.Absent)] [InlineData(VmState.Unknown)]
    public void LiveOperationAlwaysKeepsPendingRows(VmState state) => Assert.Equal(OrphanResolution.Kept,
        ReservationRules.Resolve(Row(), state, true, ArtifactPresence.Absent, Now.AddDays(1)).Resolution);
    [Theory]
    [InlineData(VmState.Running, 0, OrphanResolution.PromotedToHeld)]
    [InlineData(VmState.Unknown, 0, OrphanResolution.PromotedToHeld)]
    [InlineData(VmState.Off, 0, OrphanResolution.Kept)]
    [InlineData(VmState.Off, 11, OrphanResolution.Released)]
    public void OrphanRuntimeRequiresStateEvidence(VmState state, int minutes, OrphanResolution expected) => Assert.Equal(expected,
        ReservationRules.Resolve(Row(), state, false, ArtifactPresence.Absent, Now.AddMinutes(minutes)).Resolution);
    [Theory]
    [InlineData(ArtifactPresence.Present, OrphanResolution.PromotedToHeld)]
    [InlineData(ArtifactPresence.Absent, OrphanResolution.Released)]
    [InlineData(ArtifactPresence.Unknown, OrphanResolution.Kept)]
    public void OrphanStorageUsesArtifactEvidence(ArtifactPresence presence, OrphanResolution expected) => Assert.Equal(expected,
        ReservationRules.Resolve(Row(ReservationResource.Storage, artifact: "disk:C:\\a.vhdx"), VmState.Absent, false, presence, Now.AddDays(1)).Resolution);
    [Theory]
    [InlineData("saved-state:a")]
    [InlineData("saved-state:a-id")]
    public void SavedStateNameAndIncarnationReferToTheSameLiability(string artifact)
    {
        var amount = 8 * Gb + CapacityMath.SavedStateOverhead;
        var row = Row(ReservationResource.Storage, amount, ReservationPhase.Held, artifact: artifact);
        var inventory = Inventory(28 * Gb, Actual(state: VmState.Saved) with { SavedStateBytes = 8 * Gb });
        var accounted = CapacityMath.AccountedReservations(inventory, [row], [Vm(state: VmState.Saved)]);
        Assert.Single(accounted);
        var result = CapacityMath.Calculate(inventory, Config, [row], [Vm(state: VmState.Saved)]);
        Assert.True(result.Complete); Assert.Equal(CapacityMath.SavedStateOverhead, Assert.Single(result.Volumes).GrowthReservedBytes);
    }
}
