using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Sync.Sealing;

namespace Constructd.Tests.Store;

/// <summary>
/// A device as the tests play it, built from the key library: its key pair, its store token, the
/// account keys once it holds <c>U</c>, and its tokens at engines. It talks to a store at
/// <paramref name="storeBase"/> through <paramref name="server"/>.
/// </summary>
public sealed class TestDevice(string name, Func<HttpMessageHandler> server, Uri storeBase, TimeProvider? time = null) : IDisposable
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name { get; } = name;

    public ParticipantKey Key { get; } = ParticipantKey.Generate();

    public string Id => Key.PublicKey.Id;

    public string Actor => "device:" + Id;

    public string? Account { get; set; }

    public string? StoreToken { get; set; }

    public AccountKeys? Keys { get; private set; }

    public Dictionary<string, string> EngineTokens { get; } = new(StringComparer.Ordinal);

    /// <summary>The store without credentials.</summary>
    public HttpClient Anonymous() => new(server()) { BaseAddress = storeBase };

    /// <summary>The store with this device's token and a proof by <paramref name="signer"/> (this device's key unless given).</summary>
    public HttpClient Store(ParticipantKey? signer = null) =>
        new(new DeviceCredentialHandler(signer ?? Key, RequestProof.StoreTokenHeader, StoreToken!, _time) { InnerHandler = server() }) { BaseAddress = storeBase };

    /// <summary>An engine with this device's token there.</summary>
    public HttpClient Engine(Func<HttpMessageHandler> engineServer, Uri engineBase, string engineId, ParticipantKey? signer = null) =>
        new(new DeviceCredentialHandler(signer ?? Key, RequestProof.DeviceTokenHeader, EngineTokens[engineId], _time) { InnerHandler = engineServer() }) { BaseAddress = engineBase };

    public EnrollRequest EnrollRequest(string? account = null, string? setupCode = null, string? enrollToken = null)
    {
        var publicKey = Key.PublicKey.ToString();
        return new EnrollRequest(publicKey, Name, RequestProof.Create(Key, ProofSubjects.Enroll(publicKey), _time.GetUtcNow()), account, setupCode, enrollToken);
    }

    /// <summary>Registers this device through <paramref name="via"/> (anonymous with a code or token, or signed in).</summary>
    public async Task<HttpResponseMessage> EnrollAsync(HttpClient via, string? account = null, string? setupCode = null, string? enrollToken = null)
    {
        var response = await via.PostAsJsonAsync("devices", EnrollRequest(account, setupCode, enrollToken), StoreJson.Options);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var enrolled = (await response.Content.ReadFromJsonAsync<EnrollResponse>(StoreJson.Options))!;
            Assert.Equal(Id, enrolled.DeviceId);
            (Account, StoreToken) = (enrolled.Account, enrolled.Token);
        }

        return response;
    }

    /// <summary>The first device creates the account's keys, stores their wraps and writes its own device record.</summary>
    public async Task CreateKeysAsync()
    {
        Keys = AccountKeys.Create();
        using var store = Store();
        Assert.Equal(HttpStatusCode.NoContent, (await store.PutAsJsonAsync("keys", Keys.Wrap(), StoreJson.Options)).StatusCode);
        await WriteDeviceRecordAsync(this, DeviceRecordValue.Active);
    }

    /// <summary>Writes <paramref name="device"/>'s <c>device</c> record (its own, or another's when revoking).</summary>
    public async Task<StoredRecord> WriteDeviceRecordAsync(TestDevice device, string status)
    {
        using var store = Store();
        var id = DeviceRecords.IdOf(device.Id);
        var current = await store.GetAsync("records/" + Uri.EscapeDataString(id));
        var baseRevision = current.StatusCode == HttpStatusCode.OK ? (await current.Content.ReadFromJsonAsync<StoredRecord>(StoreJson.Options))!.Revision : 0;
        var write = DeviceRecords.Write(Keys!.Records, Keys.Epoch, Actor, new DeviceRecordValue(device.Name, device.Key.PublicKey.ToString(), status), baseRevision);
        return await PutAsync(store, write);
    }

    public async Task<StoredRecord> WriteSettingAsync(string id, JsonNode value)
    {
        using var store = Store();
        var envelope = new RecordEnvelope(id, RecordKinds.Setting, Keys!.Epoch, Actor, 0, Guid.NewGuid().ToString("n"), false);
        var sealedPayload = RecordSealer.Seal(Keys.Records, envelope, RecordPayload.Rewrite(null, value, [Actor]));
        return await PutAsync(store, new RecordWrite(id, envelope.Kind, envelope.Epoch, Actor, 0, envelope.WriteId, false, sealedPayload));
    }

    private static async Task<StoredRecord> PutAsync(HttpClient store, RecordWrite write)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "records/" + Uri.EscapeDataString(write.Id)) { Content = JsonContent.Create(write, options: StoreJson.Options) };
        request.Headers.TryAddWithoutValidation("If-Match", write.BaseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var response = await store.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StoredRecord>(StoreJson.Options))!;
    }

    // ---- pairing (8.3) ----

    /// <summary>The new device: starts a pairing; returns its id and the words this device shows.</summary>
    public async Task<(string PairingId, IReadOnlyList<string> Words)> StartPairingAsync()
    {
        using var anonymous = Anonymous();
        var response = await anonymous.PostAsJsonAsync("pairing", new PairingRequest(Key.PublicKey.ToString(), Name), StoreJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PairingCreated>(StoreJson.Options))!;
        return (created.PairingId, ComparisonWords.For(Key.PublicKey));
    }

    /// <summary>The approver: reads the pairing, computes the words, seals and signs the grant.</summary>
    public async Task<(IReadOnlyList<string> Words, HttpResponseMessage Response)> ApproveAsync(string pairingId, IReadOnlyList<EngineEndpoint>? engines = null, bool corruptSignature = false)
    {
        using var store = Store();
        var view = (await store.GetFromJsonAsync<PairingView>("pairing/" + pairingId, StoreJson.Options))!;
        var newDevice = ParticipantPublicKey.Parse(view.PublicKey);
        var grant = Pairing.Seal(new PairingGrant(Account!, Keys!.Epoch, Keys.Root, engines ?? []), newDevice, pairingId);
        var signature = Pairing.Sign(Key, pairingId, newDevice, grant);
        if (corruptSignature) signature[0] ^= 1;
        var response = await store.PutAsJsonAsync("pairing/" + pairingId, new PairingApproval(grant, signature), StoreJson.Options);
        return (ComparisonWords.For(newDevice), response);
    }

    public async Task<HttpResponseMessage> RedeemAsync(string pairingId, ParticipantKey? signer = null)
    {
        using var anonymous = Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"pairing/{pairingId}/grant");
        request.Headers.TryAddWithoutValidation(RequestProof.Header, RequestProof.Create(signer ?? Key, ProofSubjects.Redeem(pairingId), _time.GetUtcNow()));
        return await anonymous.SendAsync(request);
    }

    /// <summary>
    /// The new device: redeems the grant, checks the approver's signature, opens it, unwraps the
    /// account keys and writes its own device record. Returns the grant.
    /// </summary>
    public async Task<PairingGrant> CompletePairingAsync(string pairingId)
    {
        var response = await RedeemAsync(pairingId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var redeemed = (await response.Content.ReadFromJsonAsync<PairingRedeemed>(StoreJson.Options))!;
        Assert.True(Pairing.Verify(ParticipantPublicKey.Parse(redeemed.ApproverPublicKey), pairingId, Key.PublicKey, redeemed.Grant, redeemed.Signature));
        var grant = Pairing.Open(Key, redeemed.Grant, pairingId);
        (Account, StoreToken) = (redeemed.Account, redeemed.Token);
        await OpenKeysAsync(grant.Root);
        await WriteDeviceRecordAsync(this, DeviceRecordValue.Active);
        return grant;
    }

    /// <summary>Unwraps the account's keys from the store under <paramref name="root"/>.</summary>
    public async Task OpenKeysAsync(byte[] root)
    {
        using var store = Store();
        Keys = AccountKeys.Open(root, (await store.GetFromJsonAsync<WrappedKeys>("keys", StoreJson.Options))!);
    }

    // ---- engines ----

    public string EngineEnrollProof(string engineId) => RequestProof.Create(Key, ProofSubjects.EngineEnroll(engineId, Account!, Id), _time.GetUtcNow());

    public void Dispose()
    {
        Key.Dispose();
        Keys?.Dispose();
    }
}
