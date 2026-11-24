using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Construct.Keys;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Sync.Participation;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Agent.Tests;

/// <summary>
/// E12: a shared host on the new release while the account's store is the PC's own engine (a host that moved in place
/// during Move over). It is no part of the account's store: this device grants it no keys, it keeps the user's sign-in,
/// and its VMs keep the vault the old Companion filled in its table, answered from here as the Companion did. Keys an
/// earlier version granted it are withdrawn.
/// </summary>
public sealed class SharedHostTests
{
    [Fact]
    public async Task A_shared_host_gets_no_keys_and_its_VMs_keep_the_table_vault_with_approvals_unlocks_and_modes_from_here()
    {
        using var pc = new EngineHost();
        using var shared = new EngineHost();
        // K, carried over from the old Companion's vault key at the move (13.1).
        var key = RandomNumberGenerator.GetBytes(32);
        var (laptop, pcId) = await SetUpAsync(pc, key);
        await using var _ = laptop;

        // The old Companion left the host's table: locked, its key check, two entries sealed with K.
        var vault = shared.Services.GetRequiredService<VaultHostService>();
        await vault.PutSettingsAsync("alice", "locked", null, VaultCrypto.KeyCheck(key), CancellationToken.None);
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await vault.PutEntriesAsync("alice", [Entry(key, "db", "db_pw_123456", at), Entry(key, "npm", "npm_token_1", at)], CancellationToken.None);

        var sharedId = await laptop.AddEngineAsync(shared, await shared.AddUserAsync("alice"));
        await laptop.PostAsync($"api/engines/{sharedId}/connect", new { vaultMode = "available" });
        var grants = shared.Services.GetRequiredService<EngineKeyStore>();
        Assert.Empty(grants.GrantingAccounts(EngineGrants.Records));
        Assert.Empty(grants.GrantingAccounts(EngineGrants.Vault));
        var view = (await laptop.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == sharedId)!;
        Assert.False(view["enrolled"]!.GetValue<bool>());
        Assert.True(view["signedIn"]!.GetValue<bool>());
        // The store's engine keeps its keys.
        Assert.Equal(["alice"], pc.Services.GetRequiredService<EngineKeyStore>().GrantingAccounts(EngineGrants.Records));

        // Its VMs see the table's entries.
        var vm = await CreateVmAsync(laptop, shared, sharedId);
        var listed = await SubmitAsync(shared, vm, new { v = 1, op = "list", names = Array.Empty<string>() });
        Assert.Equal(["db", "npm"], listed.Response!["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        var mode = await laptop.GetAsync($"api/vault/engines/{sharedId}/mode");
        Assert.Equal("locked", mode["mode"]!.GetValue<string>());
        Assert.False(mode["records"]!.GetValue<bool>());

        // Locked: a value waits for a device. The allow here unlocks the VM with the key, as the Companion did.
        Assert.Equal("locked", (await SubmitAsync(shared, vm, new { v = 1, op = "get", names = new[] { "db" } })).Response!["status"]!.GetValue<string>());
        var request = await SubmitAsync(shared, vm, new { v = 1, op = "request", names = new[] { "db" }, ttl = 600 });
        Assert.NotNull(request.PendingId);
        var approvals = (await laptop.GetAsync("api/vault/approvals"))["approvals"]!.AsArray();
        Assert.Equal(sharedId, Assert.Single(approvals)!["engine"]!.GetValue<string>());
        await laptop.PostAsync($"api/vault/approvals/{sharedId}/{request.PendingId}", new { decision = "allow" }, HttpStatusCode.NoContent);
        Assert.Contains(vm.Name, (await laptop.GetAsync($"api/vault/engines/{sharedId}/mode"))["unlockedVms"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("ok", (await vault.PollAsync(vm, request.PendingId!, TimeSpan.Zero, CancellationToken.None))!["status"]!.GetValue<string>());
        var get = (await SubmitAsync(shared, vm, new { v = 1, op = "get", names = new[] { "db" } })).Response!;
        Assert.Equal("db_pw_123456", Encoding.UTF8.GetString(Convert.FromBase64String(get["secret"]!.GetValue<string>())));

        // The mode is the table's settings, switched as the Companion switched them; still no grant.
        using (var available = await laptop.Api.PutAsJsonAsync($"api/vault/engines/{sharedId}/mode", new { mode = "available" }))
        {
            var body = JsonNode.Parse(await available.Content.ReadAsStringAsync())!;
            Assert.True(available.IsSuccessStatusCode, body.ToJsonString());
            Assert.Equal("available", body["mode"]!.GetValue<string>());
            Assert.True(body["holdsKey"]!.GetValue<bool>());
            Assert.False(body["records"]!.GetValue<bool>());
        }

        // Locked again: the VM unlocked above keeps K in RAM until it stops.
        using (var locked = await laptop.Api.PutAsJsonAsync($"api/vault/engines/{sharedId}/mode", new { mode = "locked" }))
            Assert.Equal("locked", JsonNode.Parse(await locked.Content.ReadAsStringAsync())!["mode"]!.GetValue<string>());
        Assert.Empty(grants.GrantingAccounts(EngineGrants.Vault));
        Assert.Equal(["db", "npm"], (await vault.GetEntriesAsync("alice", CancellationToken.None)).Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task Keys_an_earlier_version_granted_a_shared_host_are_withdrawn_when_the_agent_starts_never_at_the_store()
    {
        using var pc = new EngineHost();
        using var shared = new EngineHost();
        var (laptop, _) = await SetUpAsync(pc, RandomNumberGenerator.GetBytes(32));
        try
        {
            var sharedId = await laptop.AddEngineAsync(shared, await shared.AddUserAsync("alice"));

            // What the earlier version left: D at the shared host (granted before its enrolment failed), and a state
            // that never asked for the withdrawal.
            var grants = shared.Services.GetRequiredService<EngineKeyStore>();
            using (var earlier = AccountKeys.Create())
            {
                foreach (var grant in EngineGrants.For(earlier, grants.HostKey.PublicKey, grants.EngineId, "alice", "locked")) grants.PutGrant("alice", grant);
            }

            var statePath = laptop.Host.Options.StatePath;
            laptop.Api.Dispose();
            await laptop.Host.DisposeAsync();
            var state = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
            var entry = state["engines"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == sharedId)!.AsObject();
            Assert.True(entry["grantsWithdrawn"]!.GetValue<bool>(), "the connect asked once already");
            entry.Remove("grantsWithdrawn");
            await File.WriteAllTextAsync(statePath, state.ToJsonString());

            laptop = await TestAgent.StartAsync(directory: laptop.Directory);
            await laptop.Host.StrayGrants;
            Assert.Empty(grants.GrantingAccounts(EngineGrants.Records));
            Assert.Contains("\"grantsWithdrawn\": true", laptop.StateFileText(), StringComparison.Ordinal);
            Assert.Equal(["alice"], pc.Services.GetRequiredService<EngineKeyStore>().GrantingAccounts(EngineGrants.Records));
        }
        finally
        {
            await laptop.DisposeAsync();
        }
    }

    internal static VaultEntry Entry(byte[] key, string name, string secret, long at) =>
        new(name, name + " entry", false, VaultCrypto.SealEntry(key, name, at, "", secret), at, "pc:old", false);

    internal static async Task<(TestAgent Laptop, string EngineId)> SetUpAsync(EngineHost engine, byte[] vaultKey, Action<DeviceAgent>? configure = null)
    {
        var signIn = await engine.AddUserAsync("alice");
        var laptop = await TestAgent.StartAsync(configure: configure);
        laptop.Host.Agent.SetupVaultKey = () => vaultKey.ToArray();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop", vaultMode = "locked" });
        return (laptop, engineId);
    }

    internal static async Task<Vm> CreateVmAsync(TestAgent laptop, EngineHost engine, string engineId)
    {
        using var created = await laptop.Api.PostAsJsonAsync($"api/engines/{engineId}/api/v1/vms", new { name = "dev", cpu = 2, ramGb = 4, diskGb = 40 });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var jobId = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while ((await laptop.GetAsync($"api/engines/{engineId}/api/v1/jobs/{jobId}"))["state"]!.GetValue<string>() is "queued" or "running")
        {
            Assert.True(DateTime.UtcNow < until, "The VM was not created in time.");
            await Task.Delay(100);
        }

        return (await engine.Services.GetRequiredService<IVmRepository>().GetAsync("dev", CancellationToken.None))!;
    }

    internal static Task<VaultSubmission> SubmitAsync(EngineHost engine, Vm vm, object request) =>
        engine.Services.GetRequiredService<VaultHostService>().SubmitAsync(vm, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request))!.AsObject(), CancellationToken.None);
}
