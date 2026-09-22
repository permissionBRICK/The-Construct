using System.Net.Http.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Delegation;
using Constructd.Tests.Support;

namespace Constructd.Tests.Windows;

public sealed class WindowsBaselineCapacityTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NoStartHoldsTemporaryRuntimeOnlyForBaselineAndReleasesIt(bool preview, bool failAfterCreate)
    {
        await using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:WindowsLicenseReuse"] = preview.ToString() });
        using var client = await Setup(app);
        var ledger = app.Service<ICapacityLedger>();
        var driver = app.Service<FakeChildVmDriver>();
        var observed = false;
        driver.Creating = descriptor =>
        {
            if (descriptor.Name != "baseline") return;
            var holds = ledger.SnapshotAsync(false, default).GetAwaiter().GetResult().Reservations.Where(r => r.VmName == "baseline").ToArray();
            Assert.Equal(preview ? 512L << 20 : 0, holds.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount));
            Assert.Equal(preview ? 2 : 0, holds.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount));
            observed = true;
        };
        if (failAfterCreate) driver.FailureAfterCreate = new IOException("injected firmware failure");
        var job = await LifecycleTests.Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()));
        Assert.True(observed);
        Assert.Equal(failAfterCreate ? JobState.Failed : JobState.Succeeded, job.State);
        Assert.DoesNotContain((await ledger.SnapshotAsync(false, default)).Reservations,
            r => r.VmName == "baseline" && r.Resource != ReservationResource.Storage);
        if (!failAfterCreate)
        {
            var vm = (await app.Vms.GetAsync("baseline", default))!;
            Assert.Equal(VmState.Off, vm.State);
            Assert.Equal(LeaseState.Inactive, vm.Lease!.State);
        }
        else Assert.Null(await app.Vms.GetAsync("baseline", default));
    }

    [Fact]
    public async Task InsufficientRuntimeCapacityRefusesFirmwareBeforeAllocatingHardware()
    {
        await using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:WindowsLicenseReuse"] = "true" });
        using var client = await Setup(app);
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Mode = CapacityMode.Enforce;
        ledger.Inventory = ledger.Inventory with { Complete = true, RamAvailableBytes = 0, Volumes = [new(@"C:\", 100L << 30, 90L << 30, 0, 0, 90L << 30)] };
        var job = await LifecycleTests.Finish(app, await client.PostAsJsonAsync("/api/v1/vms/parent/children", Request()));
        Assert.Equal(JobState.Failed, job.State);
        Assert.DoesNotContain("create:baseline", app.Service<FakeChildVmDriver>().Calls);
        Assert.Null(await app.Vms.GetAsync("baseline", default));
        Assert.DoesNotContain((await ledger.SnapshotAsync(false, default)).Reservations, r => r.VmName == "baseline");
    }

    private static async Task<HttpClient> Setup(TestApp app)
    {
        var client = await LifecycleTests.Setup(app, false);
        var store = app.Service<IMediaStore>();
        var media = (await store.GetAsync("install", default))!;
        Assert.True(await store.TryTransitionAsync(media.Id, MediaState.Ready, media with
        {
            Windows = new("win11", "en", [new("win11", "pro", "Windows 11 Pro", 1, "26200")], Prepared: true)
        }, default));
        return client;
    }
    private static object Request() => new { name = "baseline", cpus = 2, ramMb = 512, diskGb = 1, lifetime = "10m",
        media = new { installMediaId = "install" }, os = "windows", windows = "win11-pro", start = false };
}
