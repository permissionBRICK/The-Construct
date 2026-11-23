using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Api.Contracts;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Tests.Support;

namespace Constructd.Tests.Store;

/// <summary>
/// Devices against constructd (plan 8.1, 8.3, 8.4, 8.7): its built-in store and the engine itself,
/// next to the user sign-in the old clients keep using.
/// </summary>
public sealed class ConstructdIdentityTests
{
    private static readonly Uri StoreBase = new("http://localhost/api/v1/store/");
    private static readonly Uri EngineBase = new("http://localhost/api/v1/");

    [Fact]
    public async Task A_paired_device_reads_what_the_first_wrote_through_the_engine_granted_D()
    {
        using var app = new TestApp();
        using var alice = await app.CreateUserClientAsync("alice");
        var engineId = (await alice.GetFromJsonAsync<EngineIdentityResponse>("/api/v1/engine"))!.EngineId;
        var hostKey = ParticipantPublicKey.Parse((await alice.GetFromJsonAsync<EngineIdentityResponse>("/api/v1/engine"))!.HostPublicKey);

        // The signed-in user's first device; a second "first device" is refused.
        using var laptop = new TestDevice("laptop", () => app.Server.CreateHandler(), StoreBase);
        using (var signedIn = await app.CreateTokenClientAsync("alice"))
        {
            signedIn.BaseAddress = StoreBase;
            Assert.Equal(HttpStatusCode.OK, (await laptop.EnrollAsync(signedIn)).StatusCode);
            using var other = new TestDevice("other", () => app.Server.CreateHandler(), StoreBase);
            Assert.Equal(HttpStatusCode.Conflict, (await other.EnrollAsync(signedIn)).StatusCode);
        }

        Assert.Equal("alice", laptop.Account);
        await laptop.CreateKeysAsync();

        // Before the grant the engine cannot read the device records.
        using var anonymous = new HttpClient(app.Server.CreateHandler()) { BaseAddress = EngineBase };
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest("alice", laptop.Id, laptop.EngineEnrollProof(engineId)))).StatusCode);

        // The device grants the engine D (and K, the vault being available); never U.
        var grants = EngineGrants.For(laptop.Keys!, hostKey, engineId, "alice", "available");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync("/api/v1/engine/grants", new EngineGrantsRequest(grants))).StatusCode);
        var root = new EngineGrant("U", 1, SealedBox.Seal(hostKey, laptop.Keys!.Root, "x"));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync("/api/v1/engine/grants", new EngineGrantsRequest([root]))).StatusCode);
        var foreign = EngineGrants.Grant(ParticipantKey.Generate().PublicKey, engineId, "alice", "D", 1, laptop.Keys.Records);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PutAsJsonAsync("/api/v1/engine/grants", new EngineGrantsRequest([foreign]))).StatusCode);

        await laptop.WriteSettingAsync("setting:theme", JsonNode.Parse("""{"color":"green"}""")!);

        // A second device pairs through the built-in store.
        using var desktop = new TestDevice("desktop", () => app.Server.CreateHandler(), StoreBase);
        var (pairingId, words) = await desktop.StartPairingAsync();
        var (approverWords, approved) = await laptop.ApproveAsync(pairingId, [new EngineEndpoint("https://host.example", null)]);
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal(words, approverWords);
        await desktop.CompletePairingAsync(pairingId);

        // It enrols with the engine by its key and reads the record through it.
        desktop.EngineTokens[engineId] = await EnrollAsync(anonymous, desktop, engineId);
        using var desktopEngine = desktop.Engine(() => app.Server.CreateHandler(), EngineBase, engineId);
        var read = (await desktopEngine.GetFromJsonAsync<SyncRecordResponse>("sync/records/setting%3Atheme"))!;
        Assert.Equal("green", read.Value!["color"]!.GetValue<string>());
        Assert.Equal(laptop.Actor, read.Actor);

        // A device token is a full sign-in for the user's routes.
        Assert.Equal(HttpStatusCode.OK, (await desktopEngine.GetAsync("vms")).StatusCode);

        // The token with another device's key, or without a proof, is refused.
        using (var wrongKey = desktop.Engine(() => app.Server.CreateHandler(), EngineBase, engineId, laptop.Key))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKey.GetAsync("sync/records/setting%3Atheme")).StatusCode);
        }

        using (var tokenOnly = new HttpClient(app.Server.CreateHandler()) { BaseAddress = EngineBase })
        {
            tokenOnly.DefaultRequestHeaders.Add(RequestProof.DeviceTokenHeader, desktop.EngineTokens[engineId]);
            Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync("sync/records/setting%3Atheme")).StatusCode);
            // ... also next to the user's own sign-in: the device token decides.
            tokenOnly.DefaultRequestHeaders.Authorization = alice.DefaultRequestHeaders.Authorization;
            Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync("vms")).StatusCode);
        }

        // The old sign-in keeps working everywhere but for record contents.
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/whoami")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/vms")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/v1/sync/records/setting%3Atheme")).StatusCode);

        // Revocation at this engine: the device's token is refused at once, and the engine acknowledges.
        using (var store = laptop.Store())
        {
            Assert.Equal(HttpStatusCode.NoContent, (await store.DeleteAsync("devices/" + desktop.Id)).StatusCode);
        }

        await laptop.WriteDeviceRecordAsync(desktop, DeviceRecordValue.Revoked);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/devices/" + desktop.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktopEngine.GetAsync("sync/records/setting%3Atheme")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest("alice", desktop.Id, desktop.EngineEnrollProof(engineId)))).StatusCode);
        using (var store = laptop.Store())
        {
            var entry = (await store.GetFromJsonAsync<List<DeviceEntry>>("devices", StoreJson.Options))!.Single(d => d.Id == desktop.Id);
            Assert.Equal([engineId], entry.AcknowledgedBy);
        }
    }

    [Fact]
    public async Task A_device_of_one_user_does_not_enrol_as_another()
    {
        using var app = new TestApp();
        await app.AddUserAsync("bob");
        using var alice = await app.CreateUserClientAsync("alice");
        var engine = (await alice.GetFromJsonAsync<EngineIdentityResponse>("/api/v1/engine"))!;
        using var laptop = new TestDevice("laptop", () => app.Server.CreateHandler(), StoreBase);
        using (var signedIn = await app.CreateTokenClientAsync("alice"))
        {
            signedIn.BaseAddress = StoreBase;
            Assert.Equal(HttpStatusCode.OK, (await laptop.EnrollAsync(signedIn)).StatusCode);
        }

        await laptop.CreateKeysAsync();
        var grants = EngineGrants.For(laptop.Keys!, ParticipantPublicKey.Parse(engine.HostPublicKey), engine.EngineId, "alice", "locked");
        await alice.PutAsJsonAsync("/api/v1/engine/grants", new EngineGrantsRequest(grants));

        using var anonymous = new HttpClient(app.Server.CreateHandler()) { BaseAddress = EngineBase };
        var proofForBob = RequestProof.Create(laptop.Key, ProofSubjects.EngineEnroll(engine.EngineId, "bob", laptop.Id), DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest("bob", laptop.Id, proofForBob))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest("nobody", laptop.Id, proofForBob))).StatusCode);
        laptop.EngineTokens[engine.EngineId] = await EnrollAsync(anonymous, laptop, engine.EngineId);
    }

    private static async Task<string> EnrollAsync(HttpClient anonymousEngine, TestDevice device, string engineId)
    {
        var response = await anonymousEngine.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest(device.Account!, device.Id, device.EngineEnrollProof(engineId)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeviceEnrollResponse>())!.Token;
    }
}
