using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Constructd.Tests.Vault;

/// <summary>The SQLite store and the in-memory fake keep the same contract; a SQLite host survives a restart.</summary>
public sealed class VaultStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "constructd-vault-" + Guid.NewGuid().ToString("n"));

    private IVaultStore Store(bool sqlite)
    {
        if (!sqlite) return new InMemoryVaultStore();
        Directory.CreateDirectory(_root);
        var database = new SqliteDatabase(Path.Combine(_root, "vault.db"));
        database.EnsureCreated();
        return new SqliteVaultStore(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_stores_keep_the_same_contract(bool sqlite)
    {
        var store = Store(sqlite);
        var at = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(await store.GetOwnerAsync("bob", default));

        Assert.Equal(1, await store.SaveEntriesAsync("bob", [new("a", "first", true, "payload-a", 10, "pc:x", false)], default));
        Assert.Equal(2, await store.SaveEntriesAsync("BOB", [new("a", "second", false, "payload-b", 20, "vm:dev", false), new("gone", "", false, null, 5, "pc:x", true)], default));
        var entries = await store.ListEntriesAsync("bob", default);
        Assert.Equal(["a", "gone"], entries.Select(e => e.Name).Order());
        Assert.Equal(new VaultEntry("a", "second", false, "payload-b", 20, "vm:dev", false), entries.Single(e => e.Name == "a"));
        Assert.Equal(1, await store.PruneTombstonesAsync(6, default));
        Assert.Single(await store.ListEntriesAsync("bob", default));

        await store.SaveSettingsAsync("bob", "available", "check", "wrapped", default);
        Assert.Equal(new VaultOwnerState("bob", 2, "available", "check", "wrapped"), await store.GetOwnerAsync("Bob", default) is { } s ? s with { Owner = "bob" } : null);
        await store.SaveSettingsAsync("bob", "locked", "check", null, default);
        Assert.Null((await store.GetOwnerAsync("bob", default))!.WrappedKey);
        Assert.Single(await store.ListOwnersAsync(default));

        var lease = new VaultLease("l1", "bob", "dev", "a", 3, at.AddHours(1), at, "why", "approved");
        await store.SaveAsync(lease, default);
        await store.SaveAsync(lease with { UsesLeft = 2 }, default);
        Assert.Equal(lease with { UsesLeft = 2 }, (await store.ListAsync<VaultLease>("bob", default)).Single());
        Assert.Empty(await store.ListAsync<VaultLease>("carol", default));
        var job = new VaultScrubJob("j1", "bob", "dev", "apply", "agent", [new("a", "payload-b", 20)],
            [new("/root/x", "L3Jvb3QveA==", "redact", [0], "text")], "scan-1", at, at, null, ["short"]);
        await store.SaveAsync(job, default);
        var read = (await store.ListAsync<VaultScrubJob>(null, default)).Single();
        Assert.Equal(job.Items, read.Items);
        Assert.Equal(job.Files.Single().Indexes, read.Files.Single().Indexes);
        Assert.Equal(job.Unscannable, read.Unscannable);
        var decision = new VaultFileDecision("f1", "bob", "dev", "/root/x", "L3Jvb3QveA==", [0], "text", 30, [new("a", "payload-b", 20)], "scan-1", at);
        await store.SaveAsync(decision, default);
        Assert.Equal(["a"], (await store.ListAsync<VaultFileDecision>("bob", default)).Single().Names);
        await store.SaveAsync(new VaultCleanup("c1", "bob", "dev", new("a", "payload-b", 20), at), default);
        await store.DeleteAsync<VaultLease>("l1", default);
        Assert.Empty(await store.ListAsync<VaultLease>(null, default));
        Assert.Single(await store.ListAsync<VaultCleanup>("bob", default));

        await store.AddDeviceAsync(new("d1", "bob", "Phone", "hash-1", at, null), default);
        Assert.Equal("Phone", (await store.FindDeviceAsync("hash-1", default))!.Label);
        await store.TouchDeviceAsync("d1", at.AddMinutes(5), default);
        Assert.Equal(at.AddMinutes(5), (await store.ListDevicesAsync("bob", default)).Single().LastUsedAt);
        Assert.False(await store.DeleteDeviceAsync("carol", "d1", default));
        Assert.True(await store.DeleteDeviceAsync("bob", "d1", default));
        Assert.Null(await store.FindDeviceAsync("hash-1", default));

        for (var i = 0; i < 5; i++) await store.AppendEventAsync("bob", new(at.AddSeconds(i), "dev", "event " + i, i == 4), 3, default);
        var events = await store.ListEventsAsync("bob", default);
        Assert.Equal(["event 2", "event 3", "event 4"], events.Select(e => e.Text));
        Assert.True(events[^1].Warning);
    }

    [Fact]
    public async Task A_sqlite_host_keeps_entries_leases_and_the_wrapped_key_across_a_restart()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "host.db");
        string token;
        byte[] key;
        {
            using var h = await VaultHarness.CreateAsync(app: TestApp.WithSqlite(path));
            key = h.Key.ToArray();
            await h.PutEntriesAsync(h.Entry("db", "pw-123456-long"));
            await h.ApprovedAsync(h.Request("request", ["db"], new { uses = 5 }));
            token = h.GuestToken;
        }

        using var second = TestApp.WithSqlite(path);
        using var guest = second.CreateVmTokenClient(token);
        using var response = await guest.PostAsJsonAsync("/api/v1/vms/dev/vault/requests",
            new { op = "get", names = new[] { "db" }, deadline = second.Clock.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        // The lease survived, and the host master key still opens the always-available key.
        Assert.Equal("pw-123456-long", VaultHarness.Secret(answer));
        Assert.Equal(4, answer["lease"]!["usesLeft"]!.GetValue<int>());
        Assert.Equal(32, key.Length);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
