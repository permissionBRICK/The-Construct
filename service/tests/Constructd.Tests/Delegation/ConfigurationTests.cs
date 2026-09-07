using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Services;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Delegation;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardwareChangesChargeTheNextStartAndKeysReplayWithoutReapplying(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "configuration-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "state.db")) : new TestApp();
            using var owner = await LifecycleTests.Setup(app, false);
            var original = (await app.Vms.GetAsync("child", default))!;
            var request = new { cpus = 2, ramMb = 1024, operationKey = "hardware-configure" };
            (await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", request)).EnsureSuccessStatusCode();
            var changed = (await app.Vms.GetAsync("child", default))!;
            Assert.Equal(2, changed.Cpu); Assert.Equal(1024, changed.RamMb); Assert.Equal(original.Lease, changed.Lease);
            Assert.Equal(original.PowerGeneration + 1, changed.PowerGeneration);
            (await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", request)).EnsureSuccessStatusCode();
            Assert.Single(app.Service<FakeChildVmDriver>().Calls, call => call == "hardware:child");
            (await owner.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "10m" })).EnsureSuccessStatusCode();
            var reservations = (await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations.Where(r => r.VmName == "child").ToArray();
            Assert.Equal(1L << 30, reservations.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount));
            Assert.Equal(2, reservations.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount));
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", new { ramMb = 2048 })).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartiallyAppliedHardwareRecoversAfterAnAlreadyOffShutdownBumpsGeneration(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "configuration-partial-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "state.db")) : new TestApp();
            using var owner = await LifecycleTests.Setup(app, false);
            var driver = app.Service<FakeChildVmDriver>(); driver.FailureAfterHardware = new InvalidOperationException("secret failure");
            var body = new { ramMb = 1024, operationKey = "partial-hardware" };
            var failed = await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", body);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); Assert.DoesNotContain("secret", await failed.Content.ReadAsStringAsync());
            Assert.Equal(512, (await app.Vms.GetAsync("child", default))!.RamMb);
            var start = await owner.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "10m" });
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode); Assert.Contains("configuration-incomplete", await start.Content.ReadAsStringAsync());
            Assert.DoesNotContain("start:child", app.Driver.Calls);
            var beforeShutdown = (await app.Vms.GetAsync("child", default))!.PowerGeneration;
            Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "shutdown" }))).State);
            Assert.True((await app.Vms.GetAsync("child", default))!.PowerGeneration > beforeShutdown);
            driver.FailureAfterHardware = null;
            (await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", body)).EnsureSuccessStatusCode();
            Assert.Equal(1024, (await app.Vms.GetAsync("child", default))!.RamMb);
            (await owner.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "10m" })).EnsureSuccessStatusCode();
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaReferencesCoverPartialAttachmentAndDetachReplay(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "configuration-partial-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(root, "state.db")) : new TestApp();
            using var owner = await LifecycleTests.Setup(app, false);
            var media = app.Service<IMediaStore>(); var original = (await media.GetAsync("install", default))!;
            await media.AddAsync(original with { Id = "aux", Role = MediaRole.Auxiliary, Path = @"C:\media\aux.iso" }, default);
            var driver = app.Service<FakeChildVmDriver>(); driver.FailureAfterMedia = new InvalidOperationException("secret failure");
            var attach = new { auxiliaryMediaId = "aux", operationKey = "attach-auxiliary" };
            Assert.False((await owner.PutAsJsonAsync("/api/v1/vms/child/media", attach)).IsSuccessStatusCode);
            Assert.Equal(2, (await media.ListReferencesForVmAsync("child", default)).Count);
            async Task<string?> StorageProblem() => (await owner.GetFromJsonAsync<JsonElement>("/api/v1/vms/child")).GetProperty("observed").GetProperty("storageProblem").GetString();
            Assert.Equal("media-unverified", await StorageProblem());
            if (sqlite) await app.Service<CapacityReconciler>().ReconcileAsync(default);
            Assert.Equal("media-unverified", await StorageProblem());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync("/api/v1/media/aux")).StatusCode);
            driver.FailureAfterMedia = null;
            (await owner.PutAsJsonAsync("/api/v1/vms/child/media", attach)).EnsureSuccessStatusCode();
            Assert.NotEqual("media-unverified", await StorageProblem());
            var detach = new { auxiliaryMediaId = (string?)null, operationKey = "detach-auxiliary" };
            (await owner.PutAsJsonAsync("/api/v1/vms/child/media", detach)).EnsureSuccessStatusCode();
            (await owner.PutAsJsonAsync("/api/v1/vms/child/media", detach)).EnsureSuccessStatusCode();
            Assert.Equal("install", Assert.Single(await media.ListReferencesForVmAsync("child", default)).MediaId);
            Assert.Null((await driver.GetAttachedMediaAsync("child", default)).AuxiliaryPath);
            Assert.DoesNotContain(BootDevice.AuxiliaryMedia, (await app.Vms.GetAsync("child", default))!.Hardware!.BootOrder);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ExternalStartDuringConfigurationReturnsAVerificationProblemAndCanRecover()
    {
        await using var app = new TestApp(); using var owner = await LifecycleTests.Setup(app, false);
        var driver = app.Service<FakeChildVmDriver>(); driver.HardwareApplied = name => app.Driver.SetState(name, VmState.Running);
        var body = new { ramMb = 1024, operationKey = "external-start-config" };
        var result = await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", body);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode); Assert.Contains("configuration-unverified", await result.Content.ReadAsStringAsync());
        driver.HardwareApplied = null;
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "shutdown" }))).State);
        (await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", body)).EnsureSuccessStatusCode();
        Assert.Equal(1024, (await app.Vms.GetAsync("child", default))!.RamMb);
    }
    [Fact]
    public async Task DeletedIncarnationsUnfinishedConfigurationDoesNotBlockAReplacement()
    {
        await using var app = new TestApp(); using var owner = await LifecycleTests.Setup(app, false);
        var driver = app.Service<FakeChildVmDriver>(); driver.FailureAfterHardware = new InvalidOperationException("failed");
        var original = (await app.Vms.GetAsync("child", default))!;
        Assert.False((await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", new { ramMb = 1024, operationKey = "old-incarnation-config" })).IsSuccessStatusCode);
        driver.FailureAfterHardware = null;
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await owner.DeleteAsync("/api/v1/vms/child"))).State);
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await owner.PostAsJsonAsync("/api/v1/vms/parent/children", LifecycleTests.Request("child")))).State);
        Assert.NotEqual(original.Incarnation, (await app.Vms.GetAsync("child", default))!.Incarnation);
        (await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", new { ramMb = 2048 })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/vms/child/lifecycle", new { action = "start", lifetime = "10m" })).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task UnsupportedDiskGrowthLockedTemplateAndForeignMediaAreRefusedBeforeMutation()
    {
        await using var app = new TestApp(); using var owner = await LifecycleTests.Setup(app, false);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", new { diskGb = 2 })).StatusCode);
        var template = await owner.PutAsJsonAsync("/api/v1/vms/child/hardware", new { firmware = new { secureBootTemplate = "microsoftUefiCertificateAuthority" } });
        Assert.Equal(HttpStatusCode.Conflict, template.StatusCode); Assert.Contains("template-locked", await template.Content.ReadAsStringAsync());
        var media = app.Service<IMediaStore>();
        await media.AddAsync((await media.GetAsync("install", default))! with { Id = "foreign", Owner = "bob" }, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/v1/vms/child/media", new { installMediaId = "foreign" })).StatusCode);
        Assert.DoesNotContain("hardware:child", app.Service<FakeChildVmDriver>().Calls);
        Assert.DoesNotContain("media:child", app.Service<FakeChildVmDriver>().Calls);
    }
}
