using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Construct.Agent.Engines;
using Construct.Agent.State;
using Construct.Keys;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Participation;

using RecordWrite = Constructd.Store.Records.RecordWrite;
using WrappedKeys = Construct.Keys.WrappedKeys;

namespace Construct.Agent;

/// <summary>The identity flows of section 8, each one agent route.</summary>
public sealed partial class DeviceAgent
{
    private PairingSession? _pairing;

    // ---- first device (8.2, 8.5, 11.6) ----

    /// <summary>
    /// Sets this device up as the account's first device against <paramref name="engineId"/>'s built-in
    /// store, signed in there with the user's token: registers the device, creates the account keys,
    /// stores their wraps, writes the device's and the engine's records, sets the recovery code, grants
    /// the engine its keys and enrols. Returns the recovery code: the one time it leaves the agent.
    /// </summary>
    public async Task<string> SetupAsync(string engineId, string deviceName, string? vaultMode, CancellationToken ct)
    {
        string code;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireUnregistered();
            ValidateName(deviceName);
            var engine = Find(engineId) ?? throw NoEngine();
            var userToken = Reveal(engine.UserToken);
            if (userToken is null && !engine.WindowsSignIn) throw new AgentFailure(409, "not-signed-in", "The first device signs in at the engine with the user's token or Windows sign-in.");

            // A setup cut short (the agent stopped, an answer lost) is taken up again where it stopped:
            // the store registered this device and may hold the wraps of the U sealed here already.
            if (!_state.SetupPending)
            {
                // The user's token, else the Windows sign-in on the pinned connection.
                using var signedIn = new HttpClient(Handler(engine)) { BaseAddress = new Uri(new Uri(engine.Url), "api/v1/store/"), Timeout = TimeSpan.FromSeconds(30) };
                if (userToken is not null) signedIn.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
                var publicKey = _key.PublicKey.ToString();
                using var response = await signedIn.PostAsJsonAsync("devices",
                    new EnrollRequest(publicKey, deviceName, RequestProof.Create(_key, ProofSubjects.Enroll(publicKey), Time.GetUtcNow())), StoreJson.Options, ct).ConfigureAwait(false);
                await EnsureStoreAsync(response, ct).ConfigureAwait(false);
                var enrolled = (await response.Content.ReadFromJsonAsync<EnrollResponse>(StoreJson.Options, ct).ConfigureAwait(false))!;
                lock (_state)
                {
                    _state.SetupPending = true;
                    _state.DeviceName = deviceName;
                    _state.Account = enrolled.Account;
                    _state.StoreEngine = engine.Id;
                    _state.StoreToken = Hide(enrolled.Token);
                    Save();
                }
            }
            else if (_state.StoreEngine != engine.Id)
            {
                throw new AgentFailure(409, "setup-pending", "A setup against another engine is unfinished; finish it there.");
            }

            deviceName = _state.DeviceName!;
            AccountKeys? keys = null;
            if (_state.RootSeal is { } seal)
            {
                using var http = StoreHttp();
                using var response = await http.GetAsync("keys", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var root = AccountKeys.OpenRoot(_key, seal);
                    try { keys = AccountKeys.Open(root, (await response.Content.ReadFromJsonAsync<WrappedKeys>(StoreJson.Options, ct).ConfigureAwait(false))!); }
                    finally { CryptographicOperations.ZeroMemory(root); }
                }
                else if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    await EnsureStoreAsync(response, ct).ConfigureAwait(false);
                }
            }

            if (keys is null)
            {
                // The account keys: U and everything under it (8.2). U is sealed here and saved before
                // the store holds anything wrapped under it, so no answer lost in between strands it.
                long epoch;
                using (var http = StoreHttp()) epoch = await new Constructd.Store.Client.HttpStoreClient(http).ActiveEpochAsync(ct).ConfigureAwait(false);
                // An upgrade carries the old vault key over as K (13.1); otherwise K is new.
                keys = AccountKeys.Create(epoch, SetupVaultKey?.Invoke());
                lock (_state)
                {
                    _state.RootSeal = keys.SealRootTo(_key.PublicKey);
                    Save();
                }

                using var put = StoreHttp();
                using var response = await put.PutAsJsonAsync("keys", keys.Wrap(), StoreJson.Options, ct).ConfigureAwait(false);
                await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            }

            _keys = keys;
            await WriteDeviceRecordAsync(DeviceId, deviceName, _key.PublicKey.ToString(), DeviceRecordValue.Active, ct).ConfigureAwait(false);
            code = await SetRecoveryLockedAsync(ct).ConfigureAwait(false);
            await ConnectLockedAsync(engine, vaultMode ?? "locked", ct).ConfigureAwait(false);
            lock (_state)
            {
                _state.SetupPending = false;
                Save();
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed();
        return code;
    }

    /// <summary>Issues a new recovery code (8.5, replacement); the old one stops working at the store.</summary>
    public async Task<string> NewRecoveryCodeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireRegistered();
            return await SetRecoveryLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> SetRecoveryLockedAsync(CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        var code = RecoveryCode.Generate();
        var secret = code.Secret();
        try
        {
            using var http = StoreHttp();
            using var response = await http.PutAsJsonAsync("recovery", new RecoverySetup(RecoveryCode.Verifier(secret), code.SealRoot(keys, _state.Account!)), StoreJson.Options, ct).ConfigureAwait(false);
            await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            return code.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    // ---- recovery (8.5) ----

    /// <summary>
    /// Recovers the account on this fresh device: proves the code's secret to the store at
    /// <paramref name="engineId"/>, opens <c>U</c> from the recovery wrap, registers this device with the
    /// one-time enrolment token, and connects to every engine of the account.
    /// </summary>
    public async Task RecoverAsync(string engineId, string account, string recoveryCode, string deviceName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireUnregistered();
            ValidateName(deviceName);
            var engine = Find(engineId) ?? throw NoEngine();
            RecoveryCode code;
            try { code = RecoveryCode.Parse(recoveryCode); }
            catch (FormatException ex) { throw new AgentFailure(400, "invalid-code", ex.Message); }

            byte[] root;
            using (var anonymous = AnonymousStore(engine))
            {
                var secret = code.Secret();
                RecoveryResponse recovered;
                try
                {
                    using var response = await anonymous.PostAsJsonAsync("recovery", new RecoveryRequest(account, secret), StoreJson.Options, ct).ConfigureAwait(false);
                    await EnsureStoreAsync(response, ct).ConfigureAwait(false);
                    recovered = (await response.Content.ReadFromJsonAsync<RecoveryResponse>(StoreJson.Options, ct).ConfigureAwait(false))!;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }

                try { root = code.OpenRoot(recovered.Wrap, account); }
                catch (CryptographicException) { throw new AgentFailure(502, "recovery-wrap", "The store's recovery wrap does not open with this code."); }

                var publicKey = _key.PublicKey.ToString();
                using var enrol = await anonymous.PostAsJsonAsync("devices",
                    new EnrollRequest(publicKey, deviceName, RequestProof.Create(_key, ProofSubjects.Enroll(publicKey), Time.GetUtcNow()), EnrollToken: recovered.EnrollToken),
                    StoreJson.Options, ct).ConfigureAwait(false);
                await EnsureStoreAsync(enrol, ct).ConfigureAwait(false);
                var enrolled = (await enrol.Content.ReadFromJsonAsync<EnrollResponse>(StoreJson.Options, ct).ConfigureAwait(false))!;
                await AdoptAccountAsync(engine, enrolled.Account, enrolled.Token, root, deviceName, ct).ConfigureAwait(false);
            }

            CryptographicOperations.ZeroMemory(root);
            await JoinEnginesLockedAsync([], ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed();
    }

    /// <summary>
    /// Becomes a device of <paramref name="account"/>: opens the keys under <paramref name="root"/>,
    /// keeps <c>U</c> sealed to this device's key, and writes the device record.
    /// </summary>
    private async Task AdoptAccountAsync(EngineEntry storeEngine, string account, string storeToken, byte[] root, string deviceName, CancellationToken ct)
    {
        lock (_state)
        {
            _state.SetupPending = false;
            _state.DeviceName = deviceName;
            _state.Account = account;
            _state.StoreEngine = storeEngine.Id;
            _state.StoreToken = Hide(storeToken);
        }

        WrappedKeys wrapped;
        using (var http = StoreHttp())
        using (var response = await http.GetAsync("keys", ct).ConfigureAwait(false))
        {
            await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            wrapped = (await response.Content.ReadFromJsonAsync<WrappedKeys>(StoreJson.Options, ct).ConfigureAwait(false))!;
        }

        AccountKeys keys;
        try { keys = AccountKeys.Open(root, wrapped); }
        catch (CryptographicException) { throw new AgentFailure(502, "keys-do-not-open", "The store's key wraps do not open under the account's root key."); }

        lock (_state)
        {
            _state.RootSeal = keys.SealRootTo(_key.PublicKey);
            Save();
        }

        _keys = keys;
        await WriteDeviceRecordAsync(DeviceId, deviceName, _key.PublicKey.ToString(), DeviceRecordValue.Active, ct).ConfigureAwait(false);
    }

    /// <summary>After pairing or recovery: the account's engines from its records (and the grant), and a device token at each that accepts this device.</summary>
    private async Task JoinEnginesLockedAsync(IReadOnlyList<EngineEndpoint> granted, CancellationToken ct)
    {
        lock (_state)
        {
            foreach (var endpoint in granted.Where(e => e.Pin is { Length: 64 }))
            {
                Uri url;
                try { url = EngineHttp.ParseAddress(endpoint.Url); }
                catch (AgentFailure) { continue; }

                var id = EngineEntry.IdFor(Normalized(url));
                if (Find(id) is null) _state.Engines.Add(new EngineEntry { Id = id, Url = Normalized(url), Pin = endpoint.Pin!.ToUpperInvariant(), Name = url.Host });
            }

            Save();
        }

        await RefreshEnginesLockedAsync(ct).ConfigureAwait(false);
        foreach (var engine in Engines().Where(e => !e.Enrolled))
        {
            if (Find(engine.Id) is { } entry) await EnrollLockedAsync(entry, ct).ConfigureAwait(false);
        }
    }

    // ---- pairing, the new device (8.3) ----

    /// <summary>
    /// Starts pairing this device with the account whose store is at <paramref name="engineId"/>. The
    /// agent then waits for the approval by itself; the words to compare are this device's.
    /// </summary>
    public async Task<PairingView> StartPairingAsync(string engineId, string deviceName, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireUnregistered();
            ValidateName(deviceName);
            var engine = Find(engineId) ?? throw NoEngine();
            _pairing?.Cancel();
            using var anonymous = AnonymousStore(engine);
            using var response = await anonymous.PostAsJsonAsync("pairing", new PairingRequest(_key.PublicKey.ToString(), deviceName), StoreJson.Options, ct).ConfigureAwait(false);
            await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            var created = (await response.Content.ReadFromJsonAsync<PairingCreated>(StoreJson.Options, ct).ConfigureAwait(false))!;
            var session = new PairingSession(created.PairingId, engine.Id, deviceName, created.ExpiresAt, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
            _pairing = session;
            session.View = new PairingView(created.PairingId, "waiting", ComparisonWords.For(_key.PublicKey), created.ExpiresAt, engine.Url, engine.Pin, null);
            _ = Task.Run(() => WaitForGrantAsync(session));
            Events.Publish("pairing", data: System.Text.Json.JsonSerializer.SerializeToNode(session.View, StoreJson.Options));
            return session.View;
        }
        finally
        {
            _gate.Release();
        }
    }

    public PairingView? Pairing() => _pairing?.View;

    public void CancelPairing()
    {
        if (Interlocked.Exchange(ref _pairing, null) is { } session) session.Cancel();
        Changed();
    }

    /// <summary>How often the new device asks the store whether it was approved.</summary>
    public TimeSpan PairingPoll { get; init; } = TimeSpan.FromSeconds(2);

    private async Task WaitForGrantAsync(PairingSession session)
    {
        var ct = session.Stop.Token;
        try
        {
            while (!ct.IsCancellationRequested && Time.GetUtcNow() < session.ExpiresAt)
            {
                var engine = Find(session.Engine) ?? throw NoEngine();
                using (var anonymous = AnonymousStore(engine))
                using (var request = new HttpRequestMessage(HttpMethod.Get, $"pairing/{Uri.EscapeDataString(session.Id)}/grant"))
                {
                    request.Headers.TryAddWithoutValidation(RequestProof.Header, RequestProof.Create(_key, ProofSubjects.Redeem(session.Id), Time.GetUtcNow()));
                    HttpResponseMessage? response = null;
                    try { response = await anonymous.SendAsync(request, ct).ConfigureAwait(false); }
                    catch (HttpRequestException) { }

                    using (response)
                    {
                        if (response?.StatusCode == HttpStatusCode.OK)
                        {
                            var redeemed = (await response.Content.ReadFromJsonAsync<PairingRedeemed>(StoreJson.Options, ct).ConfigureAwait(false))!;
                            await CompletePairingAsync(session, engine, redeemed, ct).ConfigureAwait(false);
                            Finish(session, "done", null);
                            return;
                        }

                        if (response is { StatusCode: HttpStatusCode.Gone or HttpStatusCode.Forbidden })
                        {
                            Finish(session, "failed", "The pairing expired or was taken.");
                            return;
                        }
                    }
                }

                await Task.Delay(PairingPoll, ct).ConfigureAwait(false);
            }

            if (!ct.IsCancellationRequested) Finish(session, "failed", "The pairing expired.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (AgentFailure failure)
        {
            Finish(session, "failed", failure.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or CryptographicException)
        {
            Finish(session, "failed", "The pairing did not complete: " + ex.Message);
        }
    }

    private void Finish(PairingSession session, string state, string? problem)
    {
        session.View = session.View! with { State = state, Problem = problem };
        Events.Publish("pairing", data: System.Text.Json.JsonSerializer.SerializeToNode(session.View, StoreJson.Options));
        Changed();
    }

    /// <summary>
    /// Opens the grant once its signature checks against the approver's key, and accepts it only when
    /// that key is an active device of the account by the account's own sealed records: the store
    /// alone cannot make this device trust a key it chose (8.3: "verify sig via the device registry").
    /// </summary>
    private async Task CompletePairingAsync(PairingSession session, EngineEntry engine, PairingRedeemed redeemed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireUnregistered();
            if (!ParticipantPublicKey.TryParse(redeemed.ApproverPublicKey, out var approver) || approver!.Id != redeemed.ApproverId ||
                !Construct.Keys.Pairing.Verify(approver, session.Id, _key.PublicKey, redeemed.Grant, redeemed.Signature))
            {
                throw new AgentFailure(502, "invalid-grant", "The grant is not signed by the approving device.");
            }

            var grant = Construct.Keys.Pairing.Open(_key, redeemed.Grant, session.Id);
            if (grant.Account != redeemed.Account) throw new AgentFailure(502, "invalid-grant", "The grant names another account.");
            try
            {
                await AdoptAccountAsync(engine, grant.Account, redeemed.Token, grant.Root, session.DeviceName, ct).ConfigureAwait(false);
                var record = await ReadDeviceRecordAsync(redeemed.ApproverId, ct).ConfigureAwait(false);
                if (record is not { Status: DeviceRecordValue.Active } || record.PublicKey != approver.ToString())
                    throw new AgentFailure(502, "unknown-approver", "The approving device is not an active device of the account.");
            }
            catch
            {
                Unregister();
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(grant.Root);
            }

            await JoinEnginesLockedAsync(grant.Engines, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a half-adopted account (a pairing that failed its last check).</summary>
    private void Unregister()
    {
        lock (_state)
        {
            _state.Account = null;
            _state.StoreEngine = null;
            _state.StoreToken = null;
            _state.RootSeal = null;
            _state.DeviceName = null;
            Save();
        }

        _keys?.Dispose();
        _keys = null;
    }

    private sealed class PairingSession(string id, string engine, string deviceName, DateTimeOffset expiresAt, CancellationTokenSource stop)
    {
        public string Id { get; } = id;

        public string Engine { get; } = engine;

        public string DeviceName { get; } = deviceName;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public CancellationTokenSource Stop { get; } = stop;

        public PairingView? View { get; set; }

        public void Cancel() => Stop.Cancel();
    }

    // ---- pairing, the approver (8.3) ----

    /// <summary>A pairing to approve: the new device's name and the words computed here from its key.</summary>
    public async Task<PairingRequestView> PairingRequestAsync(string pairingId, CancellationToken ct)
    {
        RequireRegistered();
        var view = await ReadPairingAsync(pairingId, ct).ConfigureAwait(false);
        var key = ParticipantPublicKey.Parse(view.PublicKey);
        return new PairingRequestView(view.PairingId, view.Name, ComparisonWords.For(key), view.PublicKey, view.ExpiresAt);
    }

    /// <summary>
    /// Approves a pairing for the key the user compared (<paramref name="publicKey"/>, from
    /// <see cref="PairingRequestAsync"/>): seals <c>U</c> and the engines to it and signs the grant.
    /// </summary>
    public async Task ApprovePairingAsync(string pairingId, string publicKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireRegistered();
            var view = await ReadPairingAsync(pairingId, ct).ConfigureAwait(false);
            if (!string.Equals(view.PublicKey, publicKey, StringComparison.Ordinal))
                throw new AgentFailure(409, "pairing-changed", "The device asking now is not the one whose words were compared.");
            var device = ParticipantPublicKey.Parse(view.PublicKey);
            var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
            var engines = Engines().Select(e => new EngineEndpoint(e.Url, e.Pin)).ToArray();
            var sealedGrant = Construct.Keys.Pairing.Seal(new PairingGrant(_state.Account!, keys.Epoch, keys.Root, engines), device, pairingId);
            var signature = Construct.Keys.Pairing.Sign(_key, pairingId, device, sealedGrant);
            using var http = StoreHttp();
            using var response = await http.PutAsJsonAsync($"pairing/{Uri.EscapeDataString(pairingId)}", new PairingApproval(sealedGrant, signature), StoreJson.Options, ct).ConfigureAwait(false);
            await EnsureStoreAsync(response, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Constructd.Store.Identity.PairingView> ReadPairingAsync(string pairingId, CancellationToken ct)
    {
        using var http = StoreHttp();
        using var response = await http.GetAsync($"pairing/{Uri.EscapeDataString(pairingId)}", ct).ConfigureAwait(false);
        await EnsureStoreAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<Constructd.Store.Identity.PairingView>(StoreJson.Options, ct).ConfigureAwait(false))!;
    }

    // ---- devices and revocation (8.4) ----

    /// <summary>The account's devices: the store's registry with the names and status of the sealed records.</summary>
    public async Task<IReadOnlyList<DeviceListItem>> DevicesAsync(CancellationToken ct)
    {
        RequireRegistered();
        List<DeviceEntry> entries;
        using (var http = StoreHttp())
        using (var response = await http.GetAsync("devices", ct).ConfigureAwait(false))
        {
            await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            entries = (await response.Content.ReadFromJsonAsync<List<DeviceEntry>>(StoreJson.Options, ct).ConfigureAwait(false))!;
        }

        var items = new List<DeviceListItem>();
        foreach (var entry in entries)
        {
            var record = await ReadDeviceRecordAsync(entry.Id, ct).ConfigureAwait(false);
            var revoked = entry.Status != "active" || record?.Status == DeviceRecordValue.Revoked;
            items.Add(new DeviceListItem(entry.Id, record?.Name ?? entry.Name, entry.Id == DeviceId, revoked, entry.CreatedAt, entry.AcknowledgedBy));
        }

        return items;
    }

    /// <summary>
    /// Revokes another device (8.4, first release): deletes its store token, marks its record revoked,
    /// and deletes its token at every engine this device reaches. Returns which engines confirmed.
    /// </summary>
    public async Task<RevocationView> RevokeDeviceAsync(string deviceId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireRegistered();
            if (deviceId == DeviceId) throw new AgentFailure(409, "own-device", "A device is revoked from another device.");
            List<DeviceEntry> entries;
            using (var http = StoreHttp())
            using (var response = await http.GetAsync("devices", ct).ConfigureAwait(false))
            {
                await EnsureStoreAsync(response, ct).ConfigureAwait(false);
                entries = (await response.Content.ReadFromJsonAsync<List<DeviceEntry>>(StoreJson.Options, ct).ConfigureAwait(false))!;
            }

            var target = entries.FirstOrDefault(d => d.Id == deviceId) ?? throw new AgentFailure(404, "no-device", "No such device.");
            using (var http = StoreHttp())
            using (var response = await http.DeleteAsync($"devices/{Uri.EscapeDataString(deviceId)}", ct).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.NotFound) await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            }

            var record = await ReadDeviceRecordAsync(deviceId, ct).ConfigureAwait(false);
            await WriteDeviceRecordAsync(deviceId, record?.Name ?? target.Name, target.PublicKey, DeviceRecordValue.Revoked, ct).ConfigureAwait(false);

            var reached = new List<RevocationEngine>();
            foreach (var engine in Engines())
            {
                var confirmed = false;
                if (Find(engine.Id) is { } entry && (entry.DeviceToken is not null || entry.UserToken is not null))
                {
                    try
                    {
                        await Call(() => EngineApi(entry).RevokeDeviceAsync(deviceId, ct)).ConfigureAwait(false);
                        confirmed = true;
                    }
                    catch (Exception ex) when (ex is AgentFailure or TaskCanceledException)
                    {
                        // Not reached: it learns the revocation from the record at its next read (8.4).
                    }
                }

                reached.Add(new RevocationEngine(engine.Id, engine.Name, confirmed));
            }

            Changed();
            return new RevocationView(deviceId, reached);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteDeviceRecordAsync(string deviceId, string name, string publicKey, string status, CancellationToken ct) =>
        await WriteRecordAsync(DeviceRecords.IdOf(deviceId), RecordKinds.Device,
            System.Text.Json.JsonSerializer.SerializeToNode(new DeviceRecordValue(name, publicKey, status), StoreJson.Options), ct).ConfigureAwait(false);

    private async Task<DeviceRecordValue?> ReadDeviceRecordAsync(string deviceId, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var record = await new Constructd.Store.Client.HttpStoreClient(http).GetAsync(DeviceRecords.IdOf(deviceId), ct).ConfigureAwait(false);
        return record is null || record.Epoch != keys.Epoch ? null : DeviceRecords.Read(keys.Records, record);
    }

    private static void ValidateName(string? name)
    {
        if (name is not { Length: > 0 and <= 100 } || string.IsNullOrWhiteSpace(name)) throw new AgentFailure(400, "invalid-name", "A device name is 1 to 100 characters.");
    }
}

/// <summary>The new device's side of a pairing: the words it shows and what the QR code carries (store address, pin, pairing id).</summary>
public sealed record PairingView(string PairingId, string State, IReadOnlyList<string> Words, DateTimeOffset ExpiresAt, string StoreUrl, string StorePin, string? Problem);

/// <summary>The approver's view of a pairing: the name and the words it computed itself.</summary>
public sealed record PairingRequestView(string PairingId, string Name, IReadOnlyList<string> Words, string PublicKey, DateTimeOffset ExpiresAt);

public sealed record DeviceListItem(string Id, string Name, bool Me, bool Revoked, DateTimeOffset Added, IReadOnlyList<string> AcknowledgedBy);

public sealed record RevocationEngine(string Id, string Name, bool Confirmed);

public sealed record RevocationView(string DeviceId, IReadOnlyList<RevocationEngine> Engines);
