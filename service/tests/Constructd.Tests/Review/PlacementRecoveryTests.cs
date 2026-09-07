using System.Net.Http.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Tests.Delegation;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Review;

public sealed class PlacementRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservePrimaryPlacementRecoversWithoutDuplicateChargeAcrossRestart(bool existingDiskHold)
    {
        var root = Path.Combine(Path.GetTempPath(), "placement-recovery-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(root, "state.db");
        var now = DateTimeOffset.UtcNow;
        var actual = new HypervisorVmInfo("primary", "primary-id", VmState.Running, "Running", 2, 1,
            1L << 30, 1L << 30, false, null, [new(@"C:\VMs\primary.vhdx", 8L << 30, 2L << 30, null, @"C:\", true)], 0, @"C:\", true);
        var healthy = new InventorySnapshot(10, now, new(8, 32L << 30, 28L << 30,
            [new(@"C:\", 100L << 30, 80L << 30)], now), [actual], true, []);
        try
        {
            using (var app = TestApp.WithSqlite(path))
            {
                using var owner = await app.CreateUserClientAsync("alice");
                app.Service<FakeChildVmDriver>().Failure = new IOException("placement unavailable");
                var job = await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms", new { name = "primary", cpu = 1, ramGb = 1, diskGb = 16 }));
                Assert.Equal(JobState.Succeeded, job.State);
                var ledger = app.Service<SqliteCapacityLedger>();
                Assert.Contains(await ledger.ReadReservationsAsync(default), r => r.Artifact == "unresolved-primary-disk:primary");
                if (existingDiskHold)
                    Assert.True((await ledger.TryReserveAsync(new("alice", "primary", "previous-observation",
                        [new(ReservationResource.Storage, 8L << 30, @"disk:C:\VMs\primary.vhdx", @"C:\")], TimeSpan.FromMinutes(1)), default)).Allowed);
                app.Service<FakeChildVmDriver>().Failure = null;
                app.Service<FakeHypervisorInventory>().Snapshot = healthy with { Vms = [actual with { Complete = false }], Complete = false };
                await app.Service<CapacityReconciler>().ReconcileAsync(default);
                Assert.Contains(await ledger.ReadReservationsAsync(default), r => r.Artifact == "unresolved-primary-disk:primary");
                app.Service<FakeHypervisorInventory>().Snapshot = healthy;
                await app.Service<CapacityReconciler>().ReconcileAsync(default);
                await app.Service<CapacityReconciler>().ReconcileAsync(default);
                await AssertResolved(ledger);
                Assert.Contains(await app.Service<IAuditLog>().QueryAsync(100, default), a => a.Action == "capacity.placement-resolved");
            }
            var driver = new FakeHypervisorDriver(); driver.SetState("primary", VmState.Running);
            using var restarted = TestApp.WithSqlite(path, configureServices: services =>
            {
                services.AddSingleton(driver);
                services.AddSingleton(new FakeHypervisorInventory { Snapshot = healthy });
            });
            var restoredLedger = restarted.Service<SqliteCapacityLedger>();
            await restarted.Service<CapacityReconciler>().ReconcileAsync(default);
            await AssertResolved(restoredLedger);
            await restarted.Service<IHostConfigStore>().SetAsync("capacity", HostAdminDefaults.Capacity with
                { Mode = CapacityMode.Enforce, RamHeadroomBytes = 0, StorageHeadroomBytes = 0 }, "admin", default);
            var admitted = await restoredLedger.TryReserveAsync(new("alice", "next-primary", "after-recovery",
                [new(ReservationResource.Ram, 1L << 30, null, null)], TimeSpan.FromMinutes(10)), default);
            Assert.True(admitted.Allowed, admitted.Reason);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task AssertResolved(SqliteCapacityLedger ledger)
    {
        var rows = await ledger.ReadReservationsAsync(default);
        Assert.DoesNotContain(rows, r => r.Volume == "unknown" || r.Artifact?.StartsWith("unresolved-primary-disk:", StringComparison.Ordinal) == true);
        Assert.Equal(16L << 30, Assert.Single(rows, r => r.Artifact == @"disk:C:\VMs\primary.vhdx").Amount);
        Assert.Equal(@"C:\", Assert.Single(rows, ReservationRules.SavedState).Volume);
        var snapshot = await ledger.SnapshotAsync(false, default);
        Assert.True(snapshot.Complete, string.Join(",", snapshot.Problems ?? []));
    }
}
