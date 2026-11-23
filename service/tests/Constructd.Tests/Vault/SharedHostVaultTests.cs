using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Api.Composition;
using Constructd.Api.Contracts;
using Constructd.Api.Devices;
using Constructd.Api.Profiles;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Sync.Vault;
using Constructd.Tests.Store;
using Constructd.Tests.Support;

namespace Constructd.Tests.Vault;

/// <summary>
/// E12: a shared host whose engine is not the account's store (that is the user's PC). It keeps the account on the old
/// routes, its vault in the table as the Companion filled it. A device's grant of <c>D</c> there is refused; one an
/// earlier agent left behind changes nothing, and its owner withdraws it with what it brought along.
/// </summary>
public sealed class SharedHostVaultTests
{
    // PUT /engine/grants as bob, sealed to this engine as a device does.
    private static async Task<HttpResponseMessage> GrantAsync(VaultHarness h, AccountKeys keys, string mode)
    {
        var identity = (await h.Bob.GetFromJsonAsync<EngineIdentityResponse>("/api/v1/engine"))!;
        var grants = EngineGrants.For(keys, ParticipantPublicKey.Parse(identity.HostPublicKey), identity.EngineId, "bob", mode);
        return await h.Bob.PutAsJsonAsync("/api/v1/engine/grants", new EngineGrantsRequest(grants));
    }

    private static async Task<string[]> ListedAsync(VaultHarness h) =>
        (await h.AnswerAsync(h.Request("list", [])))["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task A_grant_where_the_account_store_is_elsewhere_is_refused_and_the_vault_stays_the_table()
    {
        using var h = await VaultHarness.CreateAsync("locked");
        await h.PutEntriesAsync(h.Entry("db", "db_pw_123456"), h.Entry("npm", "npm_token_1"));
        using var keys = AccountKeys.Create();

        using (var refused = await GrantAsync(h, keys, "available"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("not-participating", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }

        Assert.Empty(h.App.Service<EngineKeyStore>().GrantingAccounts(EngineGrants.Records));
        Assert.Equal(["db", "npm"], await ListedAsync(h));
        var mode = (await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/mode"))!;
        Assert.Equal("locked", mode["mode"]!.GetValue<string>());
        Assert.False(mode["records"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Keys_an_earlier_agent_left_change_nothing_and_their_withdrawal_takes_the_replica_and_keeps_the_records()
    {
        var root = Path.Combine(Path.GetTempPath(), "constructd-shared-host-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            using var h = await VaultHarness.CreateAsync("locked", TestApp.WithSqlite(Path.Combine(root, "state.db")));
            await h.PutEntriesAsync(h.Entry("db", "db_pw_123456"), h.Entry("npm", "npm_token_1"));
            using var alice = await h.App.CreateUserClientAsync("alice");

            // What the field run left: D granted by the agent before it failed to enrol, a replica of the account against
            // this engine's own store, and a record the engine wrote there for it.
            using var keys = AccountKeys.Create();
            var engine = h.App.Service<EngineKeyStore>();
            foreach (var grant in EngineGrants.For(keys, engine.HostKey.PublicKey, engine.EngineId, "bob", "locked")) engine.PutGrant("bob", grant);
            var identity = h.App.Service<EngineStoreIdentity>();
            var store = h.App.Service<RecordStore>();
            var client = identity.Client(store, "bob");
            var stray = new SecretRecordValue("stray", "", false, false, VaultSeal.SealEntry(keys.Vault, "stray", 1, "", "x"), 1, "vm:dev",
                new SecretProvenance(SecretProvenance.VmAdded, "dev"));
            Assert.IsType<WriteOutcome.Accepted>(await client.WriteAsync(
                SecretRecords.Write(keys.Records, await client.ActiveEpochAsync(default), "engine:" + identity.Id, stray, 0, false, ["vm:dev"]), default));
            var replica = Path.Combine(root, "sync", "replica-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("bob")))[..16] + ".db");
            Directory.CreateDirectory(Path.GetDirectoryName(replica)!);
            File.WriteAllText(replica, "");

            // The engine already answers as the old host did: the table's entries, no records, no replica, no device keys.
            var sync = h.App.Service<ProfileSync>();
            Assert.Equal(["db", "npm"], await ListedAsync(h));
            Assert.False((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/mode"))!["records"]!.GetValue<bool>());
            Assert.Null(sync.Replica("bob"));
            Assert.False(h.App.Service<DeviceSshKeys>().Manages("bob"));
            Assert.False((await h.Bob.GetFromJsonAsync<JsonObject>("/api/v1/engine/participation"))!["takesPart"]!.GetValue<bool>());

            // Only the caller's own keys: another user's withdrawal leaves bob's alone.
            using (var other = await alice.DeleteAsync("/api/v1/engine/grants")) Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
            Assert.Equal(["bob"], engine.GrantingAccounts(EngineGrants.Records));

            using (var withdrawn = await h.Bob.DeleteAsync("/api/v1/engine/grants")) Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
            Assert.Empty(engine.GrantingAccounts(EngineGrants.Records));
            Assert.Empty(engine.GrantingAccounts(EngineGrants.Vault));
            // The record stays (a guest's secret has no other copy); without D nothing here opens it.
            Assert.Single((await store.ExportAsync("bob", default)).Records);
            Assert.False(File.Exists(replica));

            // Idempotent; the table is untouched, and profile sync makes no replica again.
            using (var again = await h.Bob.DeleteAsync("/api/v1/engine/grants")) Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
            Assert.Equal(["db", "npm"], await ListedAsync(h));
            Assert.Equal("ok", (await h.ApprovedAsync(h.Request("request", ["db"])))["status"]!.GetValue<string>());
            await sync.SyncAsync(default);
            Assert.Null(sync.Replica("bob"));
            Assert.False(File.Exists(replica));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task The_withdrawal_is_refused_where_the_engine_takes_part_in_the_account_store()
    {
        // The engine whose own store is the account's: its keys and records stay.
        using var own = await RecordVaultHarness.CreateAsync("available");
        using (var refused = await own.Bob.DeleteAsync("/api/v1/engine/grants"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("account-store", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        }

        Assert.Equal(["bob"], own.App.Service<EngineKeyStore>().GrantingAccounts(EngineGrants.Records));
        Assert.Equal(["bob"], own.App.Service<EngineKeyStore>().GrantingAccounts(EngineGrants.Vault));
        Assert.True((await own.Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/mode"))!["records"]!.GetValue<bool>());
        Assert.True((await own.Bob.GetFromJsonAsync<JsonObject>("/api/v1/engine/participation"))!["takesPart"]!.GetValue<bool>());

        // An engine whose account syncs against a store it names takes part too.
        await using var remote = await ProfileSyncHarness.StartRemoteAsync("engine-token");
        using var keys = AccountKeys.Create();
        remote.Grant(keys);
        using (var refused = await remote.Alice.DeleteAsync("/api/v1/engine/grants")) Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal([ProfileSyncHarness.User], remote.App.Service<EngineKeyStore>().GrantingAccounts(EngineGrants.Records));
        Assert.NotNull(remote.Sync.Replica(ProfileSyncHarness.User));
        Assert.True((await remote.Alice.GetFromJsonAsync<JsonObject>("/api/v1/engine/participation"))!["takesPart"]!.GetValue<bool>());
    }
}
