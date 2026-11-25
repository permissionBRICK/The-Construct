using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Tests.Windows;
using Constructd.Windows.HyperV;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Api;

public sealed class VmStorageMoveTests
{
    private const long Gib = 1L << 30;

    private static (TestApp App, string? TempDb) CreateApp(bool sqlite = false)
    {
        string? dbPath = null;
        IDictionary<string, string?>? overrides = null;
        if (sqlite)
        {
            dbPath = Path.Combine(Path.GetTempPath(), $"constructd-storage-test-{Guid.NewGuid():n}.db");
            overrides = new Dictionary<string, string?>
            {
                ["Constructd:Persistence"] = "Sqlite",
                ["Constructd:DatabasePath"] = dbPath,
            };
        }
        var app = new TestApp(overrides);
        if (!sqlite)
        {
            var ledger = app.Service<InMemoryCapacityLedger>();
            ledger.Inventory = ledger.Inventory with
            {
                Complete = true,
                CpuLogical = 16,
                RamTotalBytes = 64 * Gib,
                RamPhysicalFreeBytes = 48 * Gib,
                RamHeadroomBytes = 4 * Gib,
                RamAvailableBytes = 44 * Gib,
                Volumes =
                [
                    new VolumeCapacity(@"C:\", 500 * Gib, 400 * Gib, 20 * Gib, 0, 380 * Gib),
                    new VolumeCapacity(@"D:\", 1000 * Gib, 900 * Gib, 20 * Gib, 0, 880 * Gib),
                ],
            };
        }
        return (app, dbPath);
    }

    private static void CleanupDb(string? dbPath)
    {
        if (dbPath is null) return;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix); } catch { }
        }
    }

    [Theory]
    [InlineData(@"D:\ConstructVMs", @"D:\ConstructVMs")]
    [InlineData(@"d:/ConstructVMs/", @"D:\ConstructVMs")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"E:\VMs\Sub Folder\", @"E:\VMs\Sub Folder")]
    public void StoragePathRules_accepts_and_normalizes_valid_windows_directories(string input, string expected)
    {
        Assert.Null(StoragePathRules.Problem(input, allowEmpty: false));
        Assert.Equal(expected, StoragePathRules.NormalizeDirectory(input));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData(@"\\server\share\vms")]
    [InlineData(@"D:\VMs\..\Other")]
    [InlineData("D:\\VMs\"quoted")]
    [InlineData(@"D:\VMs\*")]
    [InlineData("C:")]
    public void StoragePathRules_rejects_invalid_paths(string input)
    {
        Assert.NotNull(StoragePathRules.Problem(input, allowEmpty: false));
    }

    [Fact]
    public void HostConfigValidation_refuses_storage_root_on_proxmox_and_external()
    {
        var cfg = new StorageConfig(@"D:\VMs");
        Assert.Null(HostConfigValidation.UnsupportedOnPlatformReason(cfg, new ConstructdOptions { Backend = "hyperv" }));
        Assert.NotNull(HostConfigValidation.UnsupportedOnPlatformReason(cfg, new ConstructdOptions { Backend = "proxmox" }));
        Assert.NotNull(HostConfigValidation.UnsupportedOnPlatformReason(cfg, new ConstructdOptions { Backend = "external" }));
        Assert.Null(HostConfigValidation.UnsupportedOnPlatformReason(new StorageConfig(null), new ConstructdOptions { Backend = "proxmox" }));
    }

    [Fact]
    public async Task Host_config_reads_and_updates_storage_root_and_capabilities_reports_storage_move()
    {
        var (app, _) = CreateApp();
        using var _app = app;
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        var caps = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/capabilities");
        Assert.True(caps.GetProperty("policy").GetProperty("storageMove").GetBoolean());

        var initial = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/config");
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("storage").GetProperty("vmStorageRoot").ValueKind);

        var bad = await admin.PutAsJsonAsync("/api/v1/host/config", new { storage = new { vmStorageRoot = @"\\unc\share" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var updated = await admin.PutAsJsonAsync("/api/v1/host/config", new { storage = new { vmStorageRoot = @"d:/ConstructVMs/" } });
        updated.EnsureSuccessStatusCode();
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(@"D:\ConstructVMs", body.GetProperty("storage").GetProperty("vmStorageRoot").GetString());

        var cleared = await admin.PutAsJsonAsync("/api/v1/host/config", new { storage = new { vmStorageRoot = "" } });
        cleared.EnsureSuccessStatusCode();
        var clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, clearedBody.GetProperty("storage").GetProperty("vmStorageRoot").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_vm_move_relocates_disk_and_updates_capacity_reservations_without_double_counting(bool sqlite)
    {
        var (app, dbPath) = CreateApp(sqlite);
        try
        {
            using var _app = app;
            using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
            using var user = await app.CreateUserClientAsync("alice");

            await admin.CreateVmAsync("box1");
            app.Driver.DiskPaths["box1"] = @"C:\VMs\box1.vhdx";

            // Non-admin is forbidden from moving VM storage.
            var denied = await user.PostAsJsonAsync("/api/v1/vms/box1/storage/move", new { destinationPath = @"D:\ConstructVMs" });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            // Saved VMs are rejected with 409 vm-saved.
            app.Driver.SetState("box1", VmState.Saved);
            var savedRes = await admin.PostAsJsonAsync("/api/v1/vms/box1/storage/move", new { destinationPath = @"D:\ConstructVMs" });
            Assert.Equal(HttpStatusCode.Conflict, savedRes.StatusCode);
            Assert.Contains("vm-saved", await savedRes.Content.ReadAsStringAsync());

            // Move while running succeeds.
            app.Driver.SetState("box1", VmState.Running);
            var moveRes = await admin.PostAsJsonAsync("/api/v1/vms/box1/storage/move", new { destinationPath = @"D:\ConstructVMs" });
            Assert.Equal(HttpStatusCode.Accepted, moveRes.StatusCode);
            var jobId = (await moveRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
            var job = await admin.WaitForJobAsync(jobId);
            Assert.Equal(JobState.Succeeded, job.State);
            Assert.Equal(@"D:\ConstructVMs\box1.vhdx", app.Driver.DiskPaths["box1"]);

            // Capacity ledger holds exactly one disk reservation at the new path on D:\.
            var snap = await app.Service<ICapacityLedger>().SnapshotAsync(false, default);
            var diskHolds = snap.Reservations.Where(r => r.Resource == ReservationResource.Storage && !ReservationRules.SavedState(r) && r.VmName == "box1").ToArray();
            var hold = Assert.Single(diskHolds);
            Assert.Equal(@"disk:D:\ConstructVMs\box1.vhdx", hold.Artifact);
            Assert.Equal(@"D:\", hold.Volume);

            // GET /vms/box1 exposes the updated diskPath.
            var vmJson = await admin.GetFromJsonAsync<JsonElement>("/api/v1/vms/box1");
            Assert.Equal(@"D:\ConstructVMs\box1.vhdx", vmJson.GetProperty("diskPath").GetString());
        }
        finally
        {
            CleanupDb(dbPath);
        }
    }

    [Fact]
    public async Task Batch_move_all_vms_relocates_every_vm_not_already_in_destination()
    {
        var (app, _) = CreateApp();
        using var _app = app;
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        await admin.CreateVmAsync("alpha");
        await admin.CreateVmAsync("beta");
        app.Driver.DiskPaths["alpha"] = @"C:\VMs\alpha.vhdx";
        app.Driver.DiskPaths["beta"] = @"D:\Target\beta.vhdx";
        // Relocate beta's initial reservation to D:\Target so it is already in place.
        await app.Service<ICapacityLedger>().RelocateDiskAsync("beta", @"C:\VMs\beta.vhdx", @"D:\Target\beta.vhdx", @"D:\", default);

        // Update host default path first, then call /host/vms/storage/move with null destinationPath.
        (await admin.PutAsJsonAsync("/api/v1/host/config", new { storage = new { vmStorageRoot = @"D:\Target" } })).EnsureSuccessStatusCode();
        var res = await admin.PostAsJsonAsync("/api/v1/host/vms/storage/move", new { });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(@"D:\Target", body.GetProperty("destinationPath").GetString());
        var queued = body.GetProperty("vms").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["alpha"], queued);

        // Wait for alpha's move job to finish.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (app.Driver.DiskPaths["alpha"] != @"D:\Target\alpha.vhdx" && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal(@"D:\Target\alpha.vhdx", app.Driver.DiskPaths["alpha"]);
        Assert.Equal(@"D:\Target\beta.vhdx", app.Driver.DiskPaths["beta"]);
    }

    [Fact]
    public async Task HyperVDriver_uses_dynamic_storage_config_and_generates_move_storage_script()
    {
        var options = PlatformOptions.Create(o => o.VmStorageRoot = @"C:\StaticRoot");
        var config = new InMemoryHostConfigStore(new MutableClock());
        await config.SetAsync("storage", new StorageConfig(@"E:\DynamicVMs"), "admin", default);

        var moveEnvelope = """{"ok":true,"value":{"OldDiskPath":"C:\\Old\\work-vm.vhdx","NewDiskPath":"E:\\DynamicVMs\\work-vm.vhdx","NewVolume":"E:\\","Moved":true}}""";
        var runner = new RecordingProcessRunner()
            .Respond(new ProcessResult(0, """{"ok":true,"value":null}""", "", false))
            .Respond(new ProcessResult(0, moveEnvelope, "", false));

        var driver = new HyperVDriver(runner, options, NullLogger<HyperVDriver>.Instance, config);

        await driver.CreateVmAsync(new VmDescriptor("work-vm", 4, 8, 100, @"C:\isos\ubuntu.iso", Nested: true, AutomaticCheckpoints: false), null, default);
        var createScript = DecodeScript(runner[0]);
        Assert.Contains(@"$descriptor['VhdPath'] = 'E:\DynamicVMs\work-vm.vhdx'", createScript, StringComparison.Ordinal);

        var moved = await driver.MoveStorageAsync("work-vm", @"E:\DynamicVMs", null, default);
        var moveScript = DecodeScript(runner[1]);
        Assert.Contains("Move-ConstructVmStorage -Name 'work-vm' -DestinationDir 'E:\\DynamicVMs'", moveScript, StringComparison.Ordinal);
        Assert.Equal(@"C:\Old\work-vm.vhdx", moved.OldDiskPath);
        Assert.Equal(@"E:\DynamicVMs\work-vm.vhdx", moved.NewDiskPath);
        Assert.Equal(@"E:\", moved.NewVolume);
        Assert.True(moved.Moved);
    }

    private static string DecodeScript(RecordedProcess call)
    {
        var index = Array.IndexOf(call.Arguments.ToArray(), "-EncodedCommand");
        return Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[index + 1]));
    }
}
