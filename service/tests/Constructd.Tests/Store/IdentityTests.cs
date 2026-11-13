using System.Net;
using System.Net.Http.Json;
using Construct.Keys;
using Constructd.Store.Client;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;

namespace Constructd.Tests.Store;

/// <summary>
/// Devices, pairing, recovery and revocation (plan 8.1 to 8.5, 8.7, C.3) against the stand-alone
/// store, with test devices built from the key library and engines as library objects.
/// </summary>
public sealed class IdentityTests : IAsyncLifetime
{
    private readonly TempDirectory _directory = new();
    private readonly ManualTime _time = new() { Now = DateTimeOffset.UtcNow };
    private readonly StandaloneStoreFixture _store;
    private readonly List<IDisposable> _owned = [];

    public IdentityTests() => _store = new StandaloneStoreFixture(_directory.Path, _time);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var owned in _owned) owned.Dispose();
        await _store.DisposeAsync();
        _directory.Dispose();
    }

    private TestDevice Device(string name)
    {
        var device = new TestDevice(name, _store.Handler, StandaloneStoreFixture.Base, _time);
        _owned.Add(device);
        return device;
    }

    /// <summary>The first device: claims the store with the setup code and creates the account's keys.</summary>
    private async Task<TestDevice> FirstDeviceAsync()
    {
        var first = Device("laptop");
        using var anonymous = first.Anonymous();
        Assert.Equal(HttpStatusCode.OK, (await first.EnrollAsync(anonymous, "alice", _store.SetupCode)).StatusCode);
        await first.CreateKeysAsync();
        return first;
    }

    private async Task<TestDevice> PairedDeviceAsync(TestDevice approver, string name)
    {
        var device = Device(name);
        var (pairingId, words) = await device.StartPairingAsync();
        var (approverWords, approved) = await approver.ApproveAsync(pairingId);
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal(words, approverWords);
        await device.CompletePairingAsync(pairingId);
        return device;
    }

    [Fact]
    public async Task The_setup_code_claims_a_new_store_once()
    {
        var code = _store.SetupCode;
        Assert.NotNull(code);
        var first = Device("laptop");
        using var anonymous = first.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await first.EnrollAsync(anonymous, "alice")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await first.EnrollAsync(anonymous, "alice", "wrong-code")).StatusCode);
        // The proof must be by the key being registered.
        var forged = first.EnrollRequest("alice", code) with { PublicKey = ParticipantKey.Generate().PublicKey.ToString() };
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsJsonAsync("devices", forged, StoreJson.Options)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await first.EnrollAsync(anonymous, "alice", code)).StatusCode);
        Assert.Equal("alice", first.Account);

        var second = Device("phone");
        Assert.Equal(HttpStatusCode.Conflict, (await second.EnrollAsync(anonymous, "mallory", code)).StatusCode);

        using var store = first.Store();
        var devices = (await store.GetFromJsonAsync<List<DeviceEntry>>("devices", StoreJson.Options))!;
        Assert.Equal(first.Id, Assert.Single(devices).Id);
        Assert.Equal(first.Key.PublicKey.ToString(), devices[0].PublicKey);
    }

    [Fact]
    public async Task A_store_token_works_only_with_its_device_key()
    {
        var first = await FirstDeviceAsync();
        var second = await PairedDeviceAsync(first, "desktop");

        using (var own = first.Store()) Assert.Equal(HttpStatusCode.OK, (await own.GetAsync("devices")).StatusCode);
        using (var otherKey = first.Store(second.Key)) Assert.Equal(HttpStatusCode.Unauthorized, (await otherKey.GetAsync("devices")).StatusCode);

        using var tokenOnly = first.Anonymous();
        tokenOnly.DefaultRequestHeaders.Add(RequestProof.StoreTokenHeader, first.StoreToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tokenOnly.GetAsync("devices")).StatusCode);

        // A proof outside the window does not count.
        using var stale = new TestDevice("x", _store.Handler, StandaloneStoreFixture.Base, new ManualTime { Now = _time.Now - TimeSpan.FromMinutes(5) });
        stale.StoreToken = first.StoreToken;
        using (var staleClient = stale.Store(first.Key)) Assert.Equal(HttpStatusCode.Unauthorized, (await staleClient.GetAsync("devices")).StatusCode);
    }

    [Fact]
    public async Task Pairing_hands_the_new_device_the_root_key_once()
    {
        var first = await FirstDeviceAsync();
        var second = Device("desktop");
        var (pairingId, words) = await second.StartPairingAsync();

        // Not approved yet: nothing to redeem.
        Assert.Equal(HttpStatusCode.Conflict, (await second.RedeemAsync(pairingId)).StatusCode);

        var (approverWords, approved) = await first.ApproveAsync(pairingId, [new EngineEndpoint("https://engine.example", "pin")]);
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal(words, approverWords);

        // Only the device that started the pairing redeems it.
        using (var intruder = ParticipantKey.Generate())
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await second.RedeemAsync(pairingId, intruder)).StatusCode);
        }

        var grant = await second.CompletePairingAsync(pairingId);
        Assert.Equal("alice", second.Account);
        Assert.Equal(first.Keys!.Root, grant.Root);
        Assert.Equal(first.Keys.Records, second.Keys!.Records);
        Assert.Equal("https://engine.example", Assert.Single(grant.Engines).Url);

        // One redemption.
        Assert.Equal(HttpStatusCode.Gone, (await second.RedeemAsync(pairingId)).StatusCode);
        // An approved pairing is not shown or approved again.
        using var store = first.Store();
        Assert.Equal(HttpStatusCode.NotFound, (await store.GetAsync("pairing/" + pairingId)).StatusCode);

        var devices = (await store.GetFromJsonAsync<List<DeviceEntry>>("devices", StoreJson.Options))!;
        Assert.Equal(new[] { first.Id, second.Id }.Order(), devices.Select(d => d.Id).Order());
    }

    [Fact]
    public async Task A_grant_with_a_wrong_signature_is_refused()
    {
        var first = await FirstDeviceAsync();
        var second = Device("desktop");
        var (pairingId, _) = await second.StartPairingAsync();

        var (_, refused) = await first.ApproveAsync(pairingId, corruptSignature: true);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.RedeemAsync(pairingId)).StatusCode);

        // A store that swapped the grant after a correct approval is caught by the new device.
        var (_, approved) = await first.ApproveAsync(pairingId);
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        var redeemed = (await (await second.RedeemAsync(pairingId)).Content.ReadFromJsonAsync<PairingRedeemed>(StoreJson.Options))!;
        var swapped = Pairing.Seal(new PairingGrant("alice", 1, new byte[32], []), second.Key.PublicKey, pairingId);
        Assert.False(Pairing.Verify(ParticipantPublicKey.Parse(redeemed.ApproverPublicKey), pairingId, second.Key.PublicKey, swapped, redeemed.Signature));
        Assert.True(Pairing.Verify(ParticipantPublicKey.Parse(redeemed.ApproverPublicKey), pairingId, second.Key.PublicKey, redeemed.Grant, redeemed.Signature));
    }

    [Fact]
    public async Task Revoking_the_approver_voids_the_grant_it_approved()
    {
        var first = await FirstDeviceAsync();
        var second = await PairedDeviceAsync(first, "desktop");
        var third = Device("tablet");
        var (pairingId, _) = await third.StartPairingAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await second.ApproveAsync(pairingId)).Response.StatusCode);

        using (var store = first.Store())
        {
            Assert.Equal(HttpStatusCode.NoContent, (await store.DeleteAsync("devices/" + second.Id)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Gone, (await third.RedeemAsync(pairingId)).StatusCode);
    }

    [Fact]
    public async Task A_pairing_expires_after_ten_minutes()
    {
        var first = await FirstDeviceAsync();
        var second = Device("desktop");

        var (unapproved, _) = await second.StartPairingAsync();
        var (approvedId, _) = await second.StartPairingAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await first.ApproveAsync(approvedId)).Response.StatusCode);

        _time.Now += StoreDirectory.PairingLifetime + TimeSpan.FromSeconds(1);
        using var store = first.Store();
        Assert.Equal(HttpStatusCode.NotFound, (await store.GetAsync("pairing/" + unapproved)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await store.PutAsJsonAsync("pairing/" + unapproved, new PairingApproval([1], [1]), StoreJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await second.RedeemAsync(approvedId)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await second.RedeemAsync(unapproved)).StatusCode);
    }

    [Fact]
    public async Task The_recovery_code_restores_the_root_and_enrols_one_device()
    {
        var first = await FirstDeviceAsync();
        var code = RecoveryCode.Generate();
        using (var store = first.Store())
        {
            Assert.Equal(HttpStatusCode.NoContent,
                (await store.PutAsJsonAsync("recovery", new RecoverySetup(RecoveryCode.Verifier(code.Secret()), code.SealRoot(first.Keys!, "alice")), StoreJson.Options)).StatusCode);
        }

        var replacement = Device("new laptop");
        using var anonymous = replacement.Anonymous();
        Assert.Equal(HttpStatusCode.Forbidden, (await Recover(anonymous, "alice", RecoveryCode.Generate())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Recover(anonymous, "bob", code)).StatusCode);

        var response = await Recover(anonymous, "alice", RecoveryCode.Parse(code.ToString().ToLowerInvariant()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var recovered = (await response.Content.ReadFromJsonAsync<RecoveryResponse>(StoreJson.Options))!;
        var root = code.OpenRoot(recovered.Wrap, "alice");

        Assert.Equal(HttpStatusCode.OK, (await replacement.EnrollAsync(anonymous, enrollToken: recovered.EnrollToken)).StatusCode);
        Assert.Equal("alice", replacement.Account);
        await replacement.OpenKeysAsync(root);
        Assert.Equal(first.Keys!.Records, replacement.Keys!.Records);

        // The enrolment token is for one device.
        var another = Device("another");
        Assert.Equal(HttpStatusCode.Forbidden, (await another.EnrollAsync(anonymous, enrollToken: recovered.EnrollToken)).StatusCode);
    }

    [Fact]
    public async Task Recovery_attempts_are_rate_limited_per_account()
    {
        var first = await FirstDeviceAsync();
        var code = RecoveryCode.Generate();
        using (var store = first.Store())
        {
            await store.PutAsJsonAsync("recovery", new RecoverySetup(RecoveryCode.Verifier(code.Secret()), code.SealRoot(first.Keys!, "alice")), StoreJson.Options);
        }

        using var anonymous = first.Anonymous();
        for (var i = 0; i < StoreDirectory.RecoveryAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Recover(anonymous, "alice", RecoveryCode.Generate())).StatusCode);
        }

        // Even the right code waits now; another account does not.
        Assert.Equal((HttpStatusCode)429, (await Recover(anonymous, "alice", code)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Recover(anonymous, "bob", RecoveryCode.Generate())).StatusCode);

        _time.Now += StoreDirectory.RecoveryWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.OK, (await Recover(anonymous, "alice", code)).StatusCode);
    }

    [Fact]
    public async Task Replacing_the_code_swaps_verifier_and_wrap()
    {
        var first = await FirstDeviceAsync();
        var oldCode = RecoveryCode.Generate();
        var newCode = RecoveryCode.Generate();
        using var store = first.Store();
        await store.PutAsJsonAsync("recovery", new RecoverySetup(RecoveryCode.Verifier(oldCode.Secret()), oldCode.SealRoot(first.Keys!, "alice")), StoreJson.Options);
        Assert.Equal(HttpStatusCode.NoContent,
            (await store.PutAsJsonAsync("recovery", new RecoverySetup(RecoveryCode.Verifier(newCode.Secret()), newCode.SealRoot(first.Keys!, "alice")), StoreJson.Options)).StatusCode);

        using var anonymous = first.Anonymous();
        Assert.Equal(HttpStatusCode.Forbidden, (await Recover(anonymous, "alice", oldCode)).StatusCode);
        var recovered = (await (await Recover(anonymous, "alice", newCode)).Content.ReadFromJsonAsync<RecoveryResponse>(StoreJson.Options))!;
        Assert.Equal(first.Keys!.Root, newCode.OpenRoot(recovered.Wrap, "alice"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => oldCode.OpenRoot(recovered.Wrap, "alice"));

        // Wraps and keys are refused under an epoch that is not the active one.
        Assert.Equal(HttpStatusCode.Conflict,
            (await store.PutAsJsonAsync("recovery", new RecoverySetup(RecoveryCode.Verifier(newCode.Secret()), recovered.Wrap with { Epoch = 2 }), StoreJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await store.PutAsJsonAsync("keys", first.Keys.Wrap(), StoreJson.Options)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_device_is_refused_by_a_reached_engine_at_once_and_by_another_after_its_next_read()
    {
        var first = await FirstDeviceAsync();
        var second = await PairedDeviceAsync(first, "desktop");
        var reached = await EngineAsync(first, "e1");
        var unreached = await EngineAsync(first, "e2");

        foreach (var engine in new[] { reached, unreached })
        {
            var token = await engine.EnrollAsync("alice", second.Id, second.EngineEnrollProof(engine.EngineId), default);
            second.EngineTokens[engine.EngineId] = Assert.IsType<string>(token);
            Assert.Equal(("alice", second.Id), await engine.AuthenticateAsync(token, RequestProof.Create(second.Key, token, _time.Now), default));
            // The token with another device's key, and without a proof.
            Assert.Null(await engine.AuthenticateAsync(token, RequestProof.Create(first.Key, token, _time.Now), default));
            Assert.Null(await engine.AuthenticateAsync(token, null, default));
        }

        // An enrolment proof made for one engine is not accepted by another.
        Assert.Null(await unreached.EnrollAsync("alice", second.Id, second.EngineEnrollProof("e1"), default));

        // Revocation: the store's tokens go, the record is marked, the reachable engine drops its tokens.
        using (var store = first.Store())
        {
            Assert.Equal(HttpStatusCode.NoContent, (await store.DeleteAsync("devices/" + second.Id)).StatusCode);
        }

        await first.WriteDeviceRecordAsync(second, DeviceRecordValue.Revoked);
        await reached.RevokeAsync("alice", second.Id, default);

        using (var revokedStore = second.Store()) Assert.Equal(HttpStatusCode.Unauthorized, (await revokedStore.GetAsync("devices")).StatusCode);
        Assert.Null(await Authenticate(reached, second));
        // The engine that was not reached serves it until it reads the device records.
        Assert.NotNull(await Authenticate(unreached, second));
        Assert.Equal(["e1"], (await Entry(first, second.Id)).AcknowledgedBy);

        await unreached.RefreshAsync("alice", default);
        Assert.Null(await Authenticate(unreached, second));
        Assert.Null(await unreached.EnrollAsync("alice", second.Id, second.EngineEnrollProof("e2"), default));
        var entry = await Entry(first, second.Id);
        Assert.Equal("revoked", entry.Status);
        Assert.Equal(["e1", "e2"], entry.AcknowledgedBy);

        // The first device is unaffected.
        var firstToken = (await reached.EnrollAsync("alice", first.Id, first.EngineEnrollProof("e1"), default))!;
        Assert.NotNull(await reached.AuthenticateAsync(firstToken, RequestProof.Create(first.Key, firstToken, _time.Now), default));
    }

    [Fact]
    public async Task Only_an_engine_acknowledges_and_only_a_revoked_device()
    {
        var first = await FirstDeviceAsync();
        var second = await PairedDeviceAsync(first, "desktop");
        var engine = await EngineClientAsync(first, "e1");
        Assert.Equal(HttpStatusCode.Conflict, (await engine.PostAsync($"devices/{second.Id}/acknowledge", null)).StatusCode);
        using var device = first.Store();
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsync($"devices/{second.Id}/acknowledge", null)).StatusCode);
        // An engine participant is not a device: it neither reads the registry nor the key wraps.
        Assert.Equal(HttpStatusCode.Forbidden, (await engine.GetAsync("keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engine.GetAsync("devices")).StatusCode);
    }

    private static Task<HttpResponseMessage> Recover(HttpClient anonymous, string account, RecoveryCode code) =>
        anonymous.PostAsJsonAsync("recovery", new RecoveryRequest(account, code.Secret()), StoreJson.Options);

    private Task<(string Account, string DeviceId)?> Authenticate(EngineDevices engine, TestDevice device)
    {
        var token = device.EngineTokens[engine.EngineId];
        return engine.AuthenticateAsync(token, RequestProof.Create(device.Key, token, _time.Now), default);
    }

    private static async Task<DeviceEntry> Entry(TestDevice reader, string id)
    {
        using var store = reader.Store();
        return (await store.GetFromJsonAsync<List<DeviceEntry>>("devices", StoreJson.Options))!.Single(d => d.Id == id);
    }

    /// <summary>A device registers an engine with the store; the engine reads the store with its participant token.</summary>
    private async Task<HttpClient> EngineClientAsync(TestDevice device, string engineId, ParticipantKey? hostKey = null)
    {
        using var key = ParticipantKey.Generate();
        using var store = device.Store();
        var response = await store.PostAsJsonAsync("engines", new EngineRegistration(engineId, (hostKey ?? key).PublicKey.ToString()), StoreJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<EngineRegistered>(StoreJson.Options))!.Token;
        var client = new HttpClient(_store.Handler()) { BaseAddress = StandaloneStoreFixture.Base };
        client.DefaultRequestHeaders.Add(RequestProof.StoreTokenHeader, token);
        _owned.Add(client);
        return client;
    }

    /// <summary>An engine joined to the store by <paramref name="device"/>, which grants it D (8.7).</summary>
    private async Task<EngineDevices> EngineAsync(TestDevice device, string engineId)
    {
        var keys = new EngineKeyStore(":memory:", engineId);
        _owned.Add(keys);
        var http = await EngineClientAsync(device, engineId, keys.HostKey);
        foreach (var grant in EngineGrants.For(device.Keys!, keys.HostKey.PublicKey, engineId, "alice", "locked")) keys.PutGrant("alice", grant);
        var store = new HttpStoreClient(http);
        return new EngineDevices(keys, new GrantedRecordKeyProvider(keys), _ => store,
            async (account, deviceId, ct) =>
            {
                var response = await http.PostAsync($"devices/{deviceId}/acknowledge", null, ct);
                if (!response.IsSuccessStatusCode) throw new StoreUnavailableException($"acknowledge answered {(int)response.StatusCode}");
            },
            _time);
    }
}
