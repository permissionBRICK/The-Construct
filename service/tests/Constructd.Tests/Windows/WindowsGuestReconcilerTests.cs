using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
namespace Constructd.Tests.Windows;
public class WindowsGuestReconcilerTests
{
    private static async Task<(Vm Vm, FakeChildVmDriver Driver, WindowsLicenseStore Store)> Setup(TestApp app)
    {
        _ = app.CreateClient(); var driver = app.Service<FakeChildVmDriver>();
        var h = new ChildHardware(2, 4096, 80, 2, true, SecureBootTemplate.MicrosoftWindows, true, [BootDevice.Disk, BootDevice.InstallMedia], true, Os: "windows", Windows: "win11-pro");
        await driver.CreateAsync(new("windows-child", h, null, "install.iso", "answer.iso", "switch"), null, default);
        var vm = new Vm("windows-child", "alice", 2, 4, 80, app.Clock.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [], Kind: VmKind.Child,
            Parent: "parent", Incarnation: await driver.GetVmIdAsync("windows-child", default), Hardware: h);
        await app.Vms.AddAsync(vm, 5, default);
        var store = app.Service<WindowsLicenseStore>(); await store.RegisterAsync(vm, default); return (vm, driver, store);
    }
    [Fact]
    public async Task No_key_installs_detaches_and_remains_unactivated()
    {
        await using var app = new TestApp(); var (vm, driver, store) = await Setup(app);
        driver.WindowsObservation = new(200, new("win11", "pro", true, "not-activated", "3V66T"));
        await app.Service<WindowsGuestReconciler>().ReconcileAsync(default);
        var guest = Assert.Single((await store.SnapshotAsync(default)).Guests);
        Assert.Equal("installed", guest.Stage); Assert.Equal("not-activated", guest.Activation); Assert.True(guest.AuxiliaryEjected);
        Assert.Null((await driver.GetAttachedMediaAsync(vm.Name, default)).AuxiliaryPath); Assert.Null(driver.DeliveredPartialKey);
    }
    [Fact]
    public async Task Assignment_delivers_once_checks_partial_key_and_clears_secret()
    {
        await using var app = new TestApp(); var (_, driver, store) = await Setup(app);
        await store.AddAsync("win11", "pro", "retail", "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", null, null, "admin", default);
        driver.WindowsObservation = new(200, new("win11", "pro", true, "not-activated", "3V66T"));
        var worker = app.Service<WindowsGuestReconciler>(); await worker.ReconcileAsync(default); await worker.ReconcileAsync(default);
        Assert.Equal("UVWXY", driver.DeliveredPartialKey);
        Assert.Single(driver.Calls, c => c == "windows-key:windows-child");
        driver.WindowsObservation = new(220, new("win11", "pro", true, "activated", "WRONG")); await worker.ReconcileAsync(default);
        var guest = Assert.Single((await store.SnapshotAsync(default)).Guests);
        Assert.Equal("failed", guest.Activation); Assert.Equal("partial-key-mismatch", guest.Error); Assert.Null(driver.DeliveredPartialKey);
    }
    [Fact]
    public async Task Mismatched_product_does_not_deliver_or_eject()
    {
        await using var app = new TestApp(); var (_, driver, store) = await Setup(app);
        driver.WindowsObservation = new(200, new("server2022", "standard", true, "activated", "12345"));
        await app.Service<WindowsGuestReconciler>().ReconcileAsync(default);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("windows-eject:"));
        Assert.Equal("guest-product-edition-mismatch", Assert.Single((await store.SnapshotAsync(default)).Guests).Error);
    }
}
