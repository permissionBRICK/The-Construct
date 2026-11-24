using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Agent.Tests.SharedHostTests;

namespace Construct.Agent.Tests;

/// <summary>
/// E12: a shared host outside the account's store keeps the vault in its table, as the old Companion filled it. The
/// agent keeps that table and the account's <c>secret</c> records in sync both ways under the Companion's rule (the
/// newer stamp wins, on a tie the larger writer), with the payloads sealed with <c>K</c> on both sides.
/// </summary>
public sealed class TableVaultSyncTests
{
    [Fact]
    public async Task What_the_PC_adds_switches_and_deletes_reaches_the_shared_hosts_table_and_its_VMs_and_never_the_store()
    {
        using var pc = new EngineHost();
        using var shared = new EngineHost();
        var key = RandomNumberGenerator.GetBytes(32);
        var (laptop, _) = await SetUpAsync(pc, key, agent => agent.TableVaultDelay = TimeSpan.FromMilliseconds(50));
        await using var __ = laptop;
        var vault = shared.Services.GetRequiredService<VaultHostService>();
        // What the old Companion left: the table locked, with the check of K.
        await vault.PutSettingsAsync("alice", "locked", null, VaultCrypto.KeyCheck(key), CancellationToken.None);
        var sharedId = await laptop.AddEngineAsync(shared, await shared.AddUserAsync("alice"));
        await laptop.PostAsync($"api/engines/{sharedId}/connect", new { vaultMode = "locked" });
        var vm = await CreateVmAsync(laptop, shared, sharedId);

        // Added here: in the host's table, sealed as the host opens it, so a VM there gets it once allowed.
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        await HostAsync(vault, entries => Live(entries, "db") is { AutoApprove: false });
        var request = await SubmitAsync(shared, vm, new { v = 1, op = "request", names = new[] { "db" }, ttl = 600 });
        await laptop.PostAsync($"api/vault/approvals/{sharedId}/{request.PendingId}", new { decision = "allow" }, HttpStatusCode.NoContent);
        var get = (await SubmitAsync(shared, vm, new { v = 1, op = "get", names = new[] { "db" } })).Response!;
        Assert.Equal("db_pw_123456", Encoding.UTF8.GetString(Convert.FromBase64String(get["secret"]!.GetValue<string>())));

        // Auto-approve, on and off here, is what the host decides by.
        await laptop.PostAsync("api/vault/secrets", new { name = "npm", description = "npm token", value = "npm_token_1", autoApprove = true });
        await HostAsync(vault, entries => Live(entries, "npm") is { AutoApprove: true });
        Assert.Null((await SubmitAsync(shared, vm, new { v = 1, op = "request", names = new[] { "npm" } })).PendingId);
        using (var off = await laptop.Api.PutAsJsonAsync("api/vault/secrets/npm/auto-approve", new { on = false }))
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        await HostAsync(vault, entries => Live(entries, "npm") is { AutoApprove: false });
        Assert.NotNull((await SubmitAsync(shared, vm, new { v = 1, op = "request", names = new[] { "npm" } })).PendingId);

        // A delete here deletes it there and ends the lease the VM held.
        using (var deleted = await laptop.Api.DeleteAsync("api/vault/secrets/db")) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await HostAsync(vault, entries => entries.Single(e => e.Name == "db").Deleted);
        Assert.DoesNotContain(await vault.LeasesAsync("alice", CancellationToken.None), l => l.Name == "db");
        var listed = (await SubmitAsync(shared, vm, new { v = 1, op = "list", names = Array.Empty<string>() })).Response!["items"]!.AsArray();
        Assert.Equal(["npm"], listed.Select(i => i!["name"]!.GetValue<string>()));

        // The store's own engine keeps the vault as records: it is never sent a table.
        var outcomes = await laptop.Host.Agent.SyncTableVaultsAsync(CancellationToken.None);
        Assert.Equal([sharedId], outcomes.Keys);
        Assert.Null(outcomes[sharedId]);
        Assert.Empty(await pc.Services.GetRequiredService<IVaultStore>().ListEntriesAsync("alice", CancellationToken.None));
    }

    [Fact]
    public async Task What_a_VM_on_the_shared_host_adds_switches_off_and_deletes_becomes_a_record_with_that_VM_as_provenance()
    {
        using var pc = new EngineHost();
        using var shared = new EngineHost();
        var key = RandomNumberGenerator.GetBytes(32);
        var (laptop, _) = await SetUpAsync(pc, key, OnlyAsked);
        await using var __ = laptop;
        var vault = shared.Services.GetRequiredService<VaultHostService>();
        await vault.PutSettingsAsync("alice", "locked", null, VaultCrypto.KeyCheck(key), CancellationToken.None);
        var sharedId = await laptop.AddEngineAsync(shared, await shared.AddUserAsync("alice"));
        await laptop.PostAsync($"api/engines/{sharedId}/connect", new { vaultMode = "locked" });
        using (var available = await laptop.Api.PutAsJsonAsync($"api/vault/engines/{sharedId}/mode", new { mode = "available" }))
            Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        var vm = await CreateVmAsync(laptop, shared, sharedId);
        var engineId = (await laptop.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == sharedId)!["engineId"]!.GetValue<string>();

        var added = await SubmitAsync(shared, vm, new
        {
            v = 1, op = "add", names = new[] { "jira" }, description = "Jira token", autoApprove = true,
            secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("jira_token_77")),
        });
        Assert.Equal("ok", added.Response!["status"]!.GetValue<string>());
        Assert.Null((await laptop.Host.Agent.SyncTableVaultsAsync(CancellationToken.None))[sharedId]);
        var jira = await SecretAsync(laptop, "jira");
        Assert.Equal(("vm-added", "dev", engineId), Provenance(jira));
        Assert.True(jira["autoApprove"]!.GetValue<bool>());
        Assert.Equal("Jira token", jira["description"]!.GetValue<string>());
        Assert.Equal("jira_token_77", (await laptop.PostAsync("api/vault/secrets/jira/reveal", null))["value"]!.GetValue<string>());

        await SubmitAsync(shared, vm, new { v = 1, op = "autoApprove", names = new[] { "jira" }, autoApprove = false });
        await laptop.Host.Agent.SyncTableVaultsAsync(CancellationToken.None);
        jira = await SecretAsync(laptop, "jira");
        Assert.Equal(("vm-auto-approve-off", "dev", engineId), Provenance(jira));
        Assert.False(jira["autoApprove"]!.GetValue<bool>());

        // A delete there needs an allow here, then reaches the records too.
        var delete = await SubmitAsync(shared, vm, new { v = 1, op = "delete", names = new[] { "jira" } });
        await laptop.PostAsync($"api/vault/approvals/{sharedId}/{delete.PendingId}", new { decision = "allow" }, HttpStatusCode.NoContent);
        await laptop.Host.Agent.SyncTableVaultsAsync(CancellationToken.None);
        Assert.Empty((await laptop.GetAsync("api/vault/secrets")).AsArray());
        var history = (await laptop.GetAsync("api/vault/history")).AsArray();
        Assert.Equal(("vm-approved", "dev", engineId), Provenance(history.First(h => h!["action"]!.GetValue<string>() == "deleted")!));
    }

    [Fact]
    public async Task Edits_on_both_sides_resolve_by_the_newer_stamp_and_on_a_tie_by_the_larger_writer()
    {
        using var pc = new EngineHost();
        using var shared = new EngineHost();
        var key = RandomNumberGenerator.GetBytes(32);
        // Only the passes asked for here: one in between would stamp the PC's edits after the host's.
        var (laptop, _) = await SetUpAsync(pc, key, OnlyAsked);
        await using var __ = laptop;
        var vault = shared.Services.GetRequiredService<VaultHostService>();
        await vault.PutSettingsAsync("alice", "locked", null, VaultCrypto.KeyCheck(key), CancellationToken.None);
        var sharedId = await laptop.AddEngineAsync(shared, await shared.AddUserAsync("alice"));
        await laptop.PostAsync($"api/engines/{sharedId}/connect", new { vaultMode = "locked" });
        // The old Companion left c as the PC holds it, under an older stamp (the move stamps every secret anew): it stays,
        // so the host ends no lease on a value that did not change.
        var old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        await vault.PutEntriesAsync("alice", [new VaultEntry("c", "c", false, VaultCrypto.SealEntry(key, "c", old, "", "c-first"), old, "pc:old", false)],
            CancellationToken.None);
        foreach (var name in new[] { "a", "b", "c" })
            await laptop.PostAsync("api/vault/secrets", new { name, description = name, value = name + "-first" });
        await laptop.Host.Agent.SyncTableVaultsAsync(CancellationToken.None);
        var first = (await vault.GetEntriesAsync("alice", CancellationToken.None)).Entries;
        Assert.Equal([true, true, false], first.Select(e => e.UpdatedBy.StartsWith("device:", StringComparison.Ordinal)));
        Assert.Equal("pc:old", Live(first, "c")!.UpdatedBy);

        // a: the host's edit is newer. b: the PC's is. c: both carry the same stamp, and "vm:dev" > "device:…".
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await vault.PutEntriesAsync("alice", [Entry(key, "a", "a-host", now + 60_000) with { UpdatedBy = "vm:dev" }], CancellationToken.None);
        await vault.PutEntriesAsync("alice", [Entry(key, "b", "b-host", now) with { UpdatedBy = "vm:dev" }], CancellationToken.None);
        await Task.Delay(5);
        using (var b = await laptop.Api.PatchAsJsonAsync("api/vault/secrets/b", new { value = "b-pc" })) Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        using (var a = await laptop.Api.PatchAsJsonAsync("api/vault/secrets/a", new { value = "a-pc" })) Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        using (var c = await laptop.Api.PatchAsJsonAsync("api/vault/secrets/c", new { value = "c-pc" })) Assert.Equal(HttpStatusCode.OK, c.StatusCode);
        var cAt = (await SecretAsync(laptop, "c"))["updatedAt"]!.GetValue<long>();
        await vault.PutEntriesAsync("alice", [Entry(key, "c", "c-host", cAt) with { UpdatedBy = "vm:dev" }], CancellationToken.None);

        Assert.Null((await laptop.Host.Agent.SyncTableVaultsAsync(CancellationToken.None))[sharedId]);
        var table = (await vault.GetEntriesAsync("alice", CancellationToken.None)).Entries;
        foreach (var (name, value) in new[] { ("a", "a-host"), ("b", "b-pc"), ("c", "c-host") })
        {
            Assert.Equal(value, (await laptop.PostAsync($"api/vault/secrets/{name}/reveal", null))["value"]!.GetValue<string>());
            var there = Live(table, name)!;
            Assert.Equal(value, VaultCrypto.OpenEntry(key, name, there.UpdatedAt, there.Payload!).Secret);
            Assert.Equal((await SecretAsync(laptop, name))["updatedAt"]!.GetValue<long>(), there.UpdatedAt);
        }

        // In step: the next pass changes nothing on either side.
        var revision = (await vault.GetEntriesAsync("alice", CancellationToken.None)).Revision;
        await laptop.Host.Agent.SyncTableVaultsAsync(CancellationToken.None);
        Assert.Equal(revision, (await vault.GetEntriesAsync("alice", CancellationToken.None)).Revision);
    }

    // Passes run only when a test asks for one.
    private static void OnlyAsked(DeviceAgent agent) => agent.TableVaultDelay = agent.TableVaultInterval = TimeSpan.FromHours(1);

    private static VaultEntry? Live(IEnumerable<VaultEntry> entries, string name) => entries.FirstOrDefault(e => e.Name == name && !e.Deleted);

    private static async Task HostAsync(VaultHostService vault, Func<IReadOnlyList<VaultEntry>, bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!done((await vault.GetEntriesAsync("alice", CancellationToken.None)).Entries))
        {
            Assert.True(DateTime.UtcNow < until, "The host's table did not get the change in time.");
            await Task.Delay(50);
        }
    }

    private static async Task<JsonNode> SecretAsync(TestAgent laptop, string name) =>
        (await laptop.GetAsync("api/vault/secrets")).AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!;

    private static (string?, string?, string?) Provenance(JsonNode item) =>
        (item["provenance"]?["kind"]?.GetValue<string>(), item["provenance"]?["vm"]?.GetValue<string>(), item["provenance"]?["engine"]?.GetValue<string>());
}
