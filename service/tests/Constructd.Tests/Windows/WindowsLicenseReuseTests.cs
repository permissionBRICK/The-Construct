using System.Text.Json;
using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Windows;

public class WindowsLicenseReuseTests
{
    private const string ActivationId = "f69e9210-2b3d-4fcb-8e3e-ef673bc5ac8c";
    private static async Task<(Vm, FakeChildVmDriver, WindowsLicenseStore)> Setup(TestApp app, int budget = 10)
    {
        _ = app.CreateClient();
        var driver = app.Service<FakeChildVmDriver>();
        var h = new ChildHardware(2, 4096, 80, 2, true, SecureBootTemplate.MicrosoftWindows, true, [BootDevice.Disk], true, Os: "windows", Windows: "win11-pro");
        await driver.CreateAsync(new("windows-first", h, null, "install.iso", "answer.iso", "switch"), null, default);
        var vm = new Vm("windows-first", "alice", 2, 4, 80, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [],
            Kind: VmKind.Child, Parent: "parent", Incarnation: await driver.GetVmIdAsync("windows-first", default), Hardware: h);
        await app.Vms.AddAsync(vm, 5, default);
        var store = app.Service<WindowsLicenseStore>();
        await store.AddAsync("win11", "pro", "mak", "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", budget, null, "admin", default);
        Assert.Empty(await store.MachinesAsync(default));
        await store.RegisterAsync(vm, default, true);
        return (vm, driver, store);
    }
    private static WindowsGuestReport Report(TestApp app, Vm vm, string partial = "3V66T", int status = 0, WindowsActivationReport? operation = null) =>
        new("win11", "pro", true, status == 1 ? "activated" : "not-activated", partial,
            License: new(app.Clock.UtcNow, ActivationId, partial, "Volume:MAK", status, 0),
            Operation: operation, AllocationId: WindowsLicenseStore.Allocation(vm));
    private static WindowsActivationReport Prepared(WindowsActivationCommand command) =>
        new(command.Id, command.AllocationId, "prepared", new string('2', 63), "12345-12345-12345-12345", ActivationId);
    [Fact]
    public async Task Initial_proxy_activation_persists_confirmation_and_verifies_without_duplicate_online_requests()
    {
        await using var app = new TestApp(); var (vm, driver, store) = await Setup(app);
        var worker = app.Service<WindowsGuestReconciler>();
        driver.WindowsObservation = new(200, Report(app, vm)); await worker.ReconcileAsync(default);
        Assert.Equal("prepare", driver.ActivationCommand!.Action);
        driver.WindowsObservation = new(210, Report(app, vm, "UVWXY", operation: Prepared(driver.ActivationCommand)));
        await worker.ReconcileAsync(default); await worker.ReconcileAsync(default);
        Assert.Equal(1, driver.OnlineActivations); Assert.Equal("apply", driver.ActivationCommand!.Action);
        Assert.True(Assert.Single(await store.MachinesAsync(default)).HasConfirmationId);
        Assert.DoesNotContain(new string('1', 48), JsonSerializer.Serialize(await store.MachinesAsync(default)));
        Assert.DoesNotContain(new string('1', 48), JsonSerializer.Serialize((await store.SnapshotAsync(default)).Guests));
        driver.WindowsObservation = new(220, Report(app, vm, "UVWXY", 1)); await worker.ReconcileAsync(default);
        Assert.Equal("verified", Assert.Single((await store.SnapshotAsync(default)).Guests).Operation!.Stage);
        Assert.Null(driver.ActivationCommand);
        Assert.Equal(1, Assert.Single((await store.SnapshotAsync(default)).Keys).Used);
    }
    [Fact]
    public async Task Uncertain_acquisition_stays_failed_until_admin_authorizes_a_new_operation()
    {
        await using var app = new TestApp(); var (vm, driver, store) = await Setup(app);
        var worker = app.Service<WindowsGuestReconciler>();
        driver.WindowsObservation = new(200, Report(app, vm)); await worker.ReconcileAsync(default);
        driver.WindowsObservation = new(210, Report(app, vm, "UVWXY", operation: Prepared(driver.ActivationCommand!)));
        driver.ActivationFailure = new IOException();
        await worker.ReconcileAsync(default); await worker.ReconcileAsync(default);
        Assert.Equal(1, driver.OnlineActivations);
        var failed = Assert.Single((await store.SnapshotAsync(default)).Guests);
        Assert.Equal("failed", failed.Operation!.Stage);
        var authorized = await store.AuthorizeActivationAsync(vm.Name, vm.Incarnation!, failed.Operation.Id, "admin", default);
        Assert.NotEqual(failed.Operation.Id, authorized.Operation!.Id);
        await Assert.ThrowsAsync<ChildValidationException>(() => store.AuthorizeActivationAsync(vm.Name, vm.Incarnation!, failed.Operation.Id, "admin", default));
        await worker.ReconcileAsync(default);
        driver.ActivationFailure = null;
        driver.WindowsObservation = new(220, Report(app, vm, "UVWXY", operation: Prepared(driver.ActivationCommand!)));
        await worker.ReconcileAsync(default);
        Assert.Equal(2, driver.OnlineActivations);
    }
    [Fact]
    public async Task Missing_provider_does_not_charge_budget_and_stale_allocation_cannot_request_activation()
    {
        await using var app = new TestApp(); var (vm, driver, store) = await Setup(app);
        var worker = app.Service<WindowsGuestReconciler>();
        driver.WindowsObservation = new(200, Report(app, vm) with { AllocationId = "old" }); await worker.ReconcileAsync(default);
        Assert.Null(driver.ActivationCommand); Assert.Empty(await store.MachinesAsync(default));
        driver.WindowsObservation = new(210, Report(app, vm)); await worker.ReconcileAsync(default);
        driver.ActivationProviderReady = false;
        driver.WindowsObservation = new(220, Report(app, vm, "UVWXY", operation: Prepared(driver.ActivationCommand!)));
        await worker.ReconcileAsync(default);
        Assert.Equal(0, driver.OnlineActivations); Assert.Equal(0, Assert.Single((await store.SnapshotAsync(default)).Keys).Used);
    }
    [Fact]
    public async Task Reuse_matches_hardware_is_exclusive_and_replays_without_charging_exhausted_budget()
    {
        await using var app = new TestApp(); var (vm, driver, store) = await Setup(app, 1);
        var worker = app.Service<WindowsGuestReconciler>();
        driver.WindowsObservation = new(200, Report(app, vm)); await worker.ReconcileAsync(default);
        driver.WindowsObservation = new(210, Report(app, vm, "UVWXY", operation: Prepared(driver.ActivationCommand!))); await worker.ReconcileAsync(default);
        driver.WindowsObservation = new(220, Report(app, vm, "UVWXY", 1)); await worker.ReconcileAsync(default);
        await app.Service<ChildDeleteJob>().CleanupAsync(vm, new Progress<string>(), null, default);
        Assert.Equal("available", Assert.Single(await store.MachinesAsync(default)).State);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var next = vm with { Name = "windows-next", Created = app.Clock.UtcNow };
        Assert.Null(await store.ReserveMachineAsync(next with { Hardware = next.Hardware! with { RamMb = 8192 } }, default));
        Assert.Null(await store.ReserveMachineAsync(next with { Name = vm.Name }, default));
        var attempts = await Task.WhenAll(store.ReserveMachineAsync(next, default), store.ReserveMachineAsync(next with { Name = "windows-other" }, default));
        var machine = Assert.Single(attempts.OfType<WindowsLicenseMachine>());
        Assert.Equal(vm.Incarnation, machine.Incarnation);
        await driver.ReuseWindowsAsync(machine, new(next.Name, next.Hardware!, null, "install.iso", "answer.iso", "switch"), "create-next", default);
        await app.Vms.AddAsync(next, 5, default); await store.RegisterAsync(next, default, true);
        driver.WindowsObservation = new(200, Report(app, next)); await worker.ReconcileAsync(default);
        driver.WindowsObservation = new(210, Report(app, next, "UVWXY", operation: Prepared(driver.ActivationCommand!))); await worker.ReconcileAsync(default);
        Assert.Equal("apply", driver.ActivationCommand!.Action); Assert.Equal(1, driver.OnlineActivations);
        driver.WindowsObservation = new(220, Report(app, next, "UVWXY", 1)); await worker.ReconcileAsync(default); await worker.ReconcileAsync(default);
        Assert.Equal(1, Assert.Single(await store.MachinesAsync(default)).Reuses);
        Assert.Equal(1, Assert.Single((await store.SnapshotAsync(default)).Keys).Used);
    }
    [Fact]
    public async Task Never_assigned_guest_is_fully_deleted()
    {
        await using var app = new TestApp(); var (vm, driver, store) = await Setup(app);
        await app.Service<ChildDeleteJob>().CleanupAsync(vm, new Progress<string>(), null, default);
        Assert.Empty(await store.MachinesAsync(default));
        Assert.Contains(driver.Calls, c => c == "remove:" + vm.Name);
    }
    [Fact]
    public async Task Partial_cleanup_cannot_be_allocated_and_key_retirement_blocks_new_assignments()
    {
        await using var app = new TestApp(); var (vm, driver, store) = await Setup(app);
        driver.WindowsObservation = new(200, Report(app, vm));
        await app.Service<WindowsGuestReconciler>().ReconcileAsync(default);
        driver.RemoveFailure = new IOException("disk busy");
        await Assert.ThrowsAsync<IOException>(() => app.Service<ChildDeleteJob>().CleanupAsync(vm, new Progress<string>(), null, default));
        Assert.Equal("cleaning", Assert.Single(await store.MachinesAsync(default)).State);
        Assert.Null(await store.ReserveMachineAsync(vm with { Name = "another" }, default));
        var key = Assert.Single((await store.SnapshotAsync(default)).Keys);
        await Assert.ThrowsAsync<ChildValidationException>(() => store.BeginRetirementAsync(key.Id, default));
        driver.RemoveFailure = null;
        await app.Service<ChildDeleteJob>().CleanupAsync(vm, new Progress<string>(), null, default);
        await store.BeginRetirementAsync(key.Id, default);
        var waiting = await store.RegisterAsync(vm with { Name = "waiting", Incarnation = Guid.NewGuid().ToString(), Created = vm.Created.AddMinutes(1) }, default, true);
        await store.SaveAsync(waiting with { Stage = "installed" }, default);
        Assert.Null((await store.AssignAsync(waiting, null, "system", default)).KeyId);
    }

}
