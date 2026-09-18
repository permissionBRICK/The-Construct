using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.Media;
namespace Constructd.Tests.Windows;
public class WindowsLicenseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "construct-license-test-" + Guid.NewGuid().ToString("n"));
    private readonly InMemoryAuditLog audit = new();
    private WindowsLicenseStore Store() => new(Path.Combine(root, "pool.json"), new(Path.Combine(root, "keys")), audit, new MutableClock());
    private static Vm Vm(string name, string product = "win11-pro") => new(name, "alice", 2, 4, 80, DateTimeOffset.UtcNow, VmState.Running, null, null, IdlePolicy.Disabled, [],
        Kind: VmKind.Child, Parent: "primary", Incarnation: Guid.NewGuid().ToString(), Hardware: new(2, 4096, 80, 2, true, SecureBootTemplate.MicrosoftWindows, true, [], true, Os: "windows", Windows: product));
    [Fact]
    public async Task Keys_are_encrypted_and_survive_reload_without_leaking_to_views_or_audit()
    {
        const string secret = "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY";
        var store = Store(); var info = await store.AddAsync("win11", "pro", "retail", secret, null, "lab", "admin", default);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "pool.json")));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(await audit.QueryAsync(100, default)));
        Assert.Equal(info, Assert.Single((await Store().SnapshotAsync(default)).Keys));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "keys", "windows-master.key")));
    }
    [Fact]
    public async Task Concurrent_retail_assignment_is_exclusive_and_product_must_match()
    {
        var store = Store(); var key = await store.AddAsync("win11", "pro", "retail", "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", null, null, "admin", default);
        var guests = new List<WindowsGuestStatus>();
        foreach (var name in new[] { "one", "two" }) { var g = (await store.RegisterAsync(Vm(name), default)) with { Stage = "installed" }; await store.SaveAsync(g, default); guests.Add(g); }
        var assigned = await Task.WhenAll(guests.Select(g => store.AssignAsync(g, null, "system", default)));
        Assert.Single(assigned.Where(g => g.KeyId == key.Id));
        var server = (await store.RegisterAsync(Vm("server", "server2025-standard"), default)) with { Stage = "installed" }; await store.SaveAsync(server, default);
        await Assert.ThrowsAsync<ChildValidationException>(() => store.AssignAsync(server, key.Id, "admin", default));
    }
    [Fact]
    public async Task Mak_budget_is_not_refunded_on_delete_or_double_charged_on_retry()
    {
        var store = Store(); var key = await store.AddAsync("server2022", "standard", "mak", "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", 1, null, "admin", default);
        var guest = (await store.RegisterAsync(Vm("one", "server2022-standard"), default)) with { Stage = "installed" }; await store.SaveAsync(guest, default);
        guest = await store.AssignAsync(guest, null, "system", default);
        await store.BeginActivationAsync(guest, default); await store.BeginActivationAsync(guest, default);
        await store.ReleaseAsync(guest.VmName, guest.Incarnation, default);
        Assert.Equal(1, Assert.Single((await store.SnapshotAsync(default)).Keys).Used);
        var next = (await store.RegisterAsync(Vm("two", "server2022-standard"), default)) with { Stage = "installed" }; await store.SaveAsync(next, default);
        Assert.Null((await store.AssignAsync(next, null, "system", default)).KeyId);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
