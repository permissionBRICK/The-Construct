using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Construct.Agent.Engines;
using Construct.Agent.Events;
using Construct.Agent.Protection;
using Construct.Agent.State;
using Construct.Keys;
using Constructd.Client;
using Constructd.Store.Client;
using Constructd.Store.Records;

using RecordWrite = Constructd.Store.Records.RecordWrite;
using WrappedKeys = Construct.Keys.WrappedKeys;

namespace Construct.Agent;

/// <summary>
/// The device agent's core (5.1): the device key pair and <c>U</c>, the engines its user works with,
/// and the identity flows of section 8 (first device, pairing both ways, recovery, revocation,
/// enrolment and key grants). It owns no VM logic; everything about VMs is a call to an engine,
/// which the local API proxies (<see cref="Http.EngineProxy"/>). Nothing here hands a key, a token
/// or <c>U</c> to a caller: the only secret that leaves is a new recovery code, once, for the user.
/// </summary>
public sealed partial class DeviceAgent : IDisposable
{
    private readonly AgentOptions _options;
    private readonly IDataProtection _protection;
    private readonly ParticipantKey _key;
    private readonly AgentState _state;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private AccountKeys? _keys;

    /// <param name="protection">The platform's protection for a given recorded choice; Linux's by default.</param>
    public DeviceAgent(AgentOptions options, Func<string?, IDataProtection>? protection = null)
    {
        _options = options;
        PrivateFiles.EnsureDirectory(options.DataDirectory);
        _state = StateFile.Load(options.StatePath);
        _protection = protection?.Invoke(_state.Protection) ?? LinuxProtection.Open(options.DataDirectory, _state.Protection, options.Protection);
        if (_state.DeviceKey is null)
        {
            // First run: the device key pair, kept only protected.
            _key = ParticipantKey.Generate();
            _state.Protection = _protection.Name;
            _state.DeviceKey = Hide(_key.Export());
            Save();
        }
        else
        {
            _key = ParticipantKey.Import(Reveal(_state.DeviceKey)!);
        }

        Events = new AgentEvents();
        Streams = new EngineStreams(Events, EngineClientFor, () => Engines().Select(e => e.Id), _stopping.Token);
    }

    public AgentEvents Events { get; }

    /// <summary>The engines' change streams on <see cref="Events"/>; the host starts them (<see cref="EngineStreams.Sync"/>).</summary>
    public EngineStreams Streams { get; }

    public string DeviceId => _key.PublicKey.Id;

    private TimeProvider Time => _options.Time;

    private string Actor => "device:" + DeviceId;

    // ---- views ----

    public DeviceView Device() => new(DeviceId, _state.DeviceName, _state.Account, _state.Registered, _state.StoreEngine,
        _protection.Name, _protection.Weak);

    public IReadOnlyList<EngineView> Engines()
    {
        lock (_state) return _state.Engines.Select(View).ToArray();
    }

    private EngineView View(EngineEntry e) =>
        new(e.Id, e.Name, e.Url, e.Pin, e.EngineId, e.DeviceToken is not null, e.UserToken is not null, e.Id == _state.StoreEngine);

    // ---- engines (4) ----

    /// <summary>Reads the certificate an engine presents, for the user to confirm before it is added.</summary>
    public async Task<EngineProbe> ProbeAsync(string address, CancellationToken ct)
    {
        var url = EngineHttp.ParseAddress(address);
        var (pin, status) = await EngineHttp.ProbeAsync(url, ct).ConfigureAwait(false);
        return new EngineProbe(Normalized(url), pin, status, Find(EngineEntry.IdFor(Normalized(url)))?.Id);
    }

    /// <summary>The solo engine on this machine, on loopback, or null when none answers.</summary>
    public async Task<EngineProbe?> ProbeLocalAsync(CancellationToken ct)
    {
        try
        {
            return await ProbeAsync($"https://127.0.0.1:{_options.LocalEnginePort}", ct).ConfigureAwait(false);
        }
        catch (AgentFailure failure) when (failure.Code == "engine-unreachable")
        {
            return null;
        }
    }

    /// <summary>
    /// Adds an engine with the pin the user confirmed. <paramref name="userToken"/> is the user's sign-in
    /// there (an API token), needed until this device holds a device token at that engine. When this
    /// device is registered, the engine is also written as an <c>engine</c> record and connected.
    /// </summary>
    public async Task<EngineView> AddEngineAsync(string address, string pin, string? name, string? userToken, CancellationToken ct)
    {
        var url = Normalized(EngineHttp.ParseAddress(address));
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw new AgentFailure(400, "invalid-pin", "A pin is the certificate's SHA-256 in hex.");
        EngineEntry entry;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_state)
            {
                entry = Find(EngineEntry.IdFor(url)) ?? new EngineEntry { Id = EngineEntry.IdFor(url), Url = url, Pin = pin.ToUpperInvariant(), Name = name ?? new Uri(url).Host };
                if (!_state.Engines.Contains(entry)) _state.Engines.Add(entry);
                // Adding a known engine again takes the newly confirmed pin (a renewed certificate).
                entry.Pin = pin.ToUpperInvariant();
                if (name is { Length: > 0 }) entry.Name = name;
                if (userToken is { Length: > 0 }) entry.UserToken = Hide(userToken);
                Save();
            }

            Forget(entry.Id);
            if (_state.Registered) await ConnectLockedAsync(entry, vaultMode: null, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed();
        return View(entry);
    }

    public async Task RemoveEngineAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Find(id) ?? throw NoEngine();
            if (entry.Id == _state.StoreEngine) throw new AgentFailure(409, "store-engine", "This engine holds the account's store; it stays.");
            if (_state.Registered) await DeleteEngineRecordAsync(entry, ct).ConfigureAwait(false);
            lock (_state)
            {
                _state.Engines.Remove(entry);
                Save();
            }

            Forget(id);
        }
        finally
        {
            _gate.Release();
        }

        Changed();
    }

    /// <summary>Grants the engine its keys (8.7: <c>D</c>, and <c>K</c> when <paramref name="vaultMode"/> is <c>available</c>) and enrols this device there.</summary>
    public async Task<EngineView> ConnectEngineAsync(string id, string? vaultMode, CancellationToken ct)
    {
        EngineEntry entry;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireRegistered();
            entry = Find(id) ?? throw NoEngine();
            await ConnectLockedAsync(entry, vaultMode ?? "locked", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed();
        return View(entry);
    }

    /// <summary>
    /// Learns the engine's identity, writes its <c>engine</c> record, grants keys when asked (or when
    /// this device has no token there yet) and enrols. A failure to enrol leaves the engine usable with
    /// the user's sign-in and is reported by <see cref="EngineView.Enrolled"/>.
    /// </summary>
    private async Task ConnectLockedAsync(EngineEntry entry, string? vaultMode, CancellationToken ct)
    {
        var engine = EngineApi(entry);
        if (entry.EngineId is null)
        {
            var identity = await Call(() => engine.GetEngineIdentityAsync(ct)).ConfigureAwait(false);
            lock (_state)
            {
                entry.EngineId = identity.EngineId;
                entry.HostPublicKey = identity.HostPublicKey;
                Save();
            }
        }

        await WriteEngineRecordAsync(entry, ct).ConfigureAwait(false);
        if (vaultMode is not null || entry.DeviceToken is null)
        {
            var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
            var grants = EngineGrants.For(keys, ParticipantPublicKey.Parse(entry.HostPublicKey!), entry.EngineId!, _state.Account!, vaultMode ?? "locked");
            await Call(() => engine.PutEngineGrantsAsync(new EngineGrantsRequest
            {
                Grants = grants.Select(g => new Constructd.Client.EngineGrant { Name = g.Name, Epoch = g.Epoch, Sealed = g.Sealed }).ToList(),
            }, ct)).ConfigureAwait(false);
        }

        if (entry.DeviceToken is null) await EnrollLockedAsync(entry, ct).ConfigureAwait(false);
        else await RegisterSshKeyLockedAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The device's SSH public key line; the device features set it (<see cref="Device.DeviceServices"/>).
    /// Registered at every engine this device enrols at, so the engine authorizes it on the user's VMs.
    /// </summary>
    public Func<CancellationToken, Task<string>>? SshPublicKey { get; set; }

    /// <summary>
    /// Registers this device's SSH key at an engine where it holds a device token. A failure leaves the
    /// device without SSH to that engine's VMs until the next enrolment or connect; it is not an error
    /// of the flow that enrolled.
    /// </summary>
    private async Task RegisterSshKeyLockedAsync(EngineEntry entry, CancellationToken ct)
    {
        if (SshPublicKey is not { } read || entry.DeviceToken is null) return;
        try
        {
            var key = await read(ct).ConfigureAwait(false);
            await EngineApi(entry).PutDeviceSshKeyAsync(new DeviceSshKeyRequest { PublicKey = key }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentFailure or ApiException or HttpRequestException)
        {
        }
    }

    /// <summary>Asks the engine for a device token by this device's key (8.1); false when the engine refuses.</summary>
    private async Task<bool> EnrollLockedAsync(EngineEntry entry, CancellationToken ct)
    {
        // The engine's id comes from its record or from GET /engine with a sign-in.
        if (entry.EngineId is null) return false;

        var proof = RequestProof.Create(_key, Constructd.Store.Identity.ProofSubjects.EngineEnroll(entry.EngineId, _state.Account!, DeviceId), Time.GetUtcNow());
        try
        {
            var answer = await new ConstructdClient(Client(entry)).EnrollDeviceAsync(new DeviceEnrollRequest
            {
                Account = _state.Account!,
                DeviceId = DeviceId,
                Proof = proof,
            }, ct).ConfigureAwait(false);
            lock (_state)
            {
                entry.DeviceToken = Hide(answer.Token);
                entry.UserToken = null;
                Save();
            }

            await RegisterSshKeyLockedAsync(entry, ct).ConfigureAwait(false);
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 409)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ---- the engine records (9.1, 9.2) ----

    private sealed record EngineRecordValue(string Url, string Pin, string Name, string? EngineId, string? HostPublicKey, string Status);

    private static string EngineRecordId(EngineEntry entry) => "engine:" + entry.Id;

    private async Task WriteEngineRecordAsync(EngineEntry entry, CancellationToken ct)
    {
        var value = new EngineRecordValue(entry.Url, entry.Pin, entry.Name, entry.EngineId, entry.HostPublicKey, "trusted");
        await WriteRecordAsync(EngineRecordId(entry), RecordKinds.Engine, System.Text.Json.JsonSerializer.SerializeToNode(value, StoreJson.Options), ct).ConfigureAwait(false);
    }

    private async Task DeleteEngineRecordAsync(EngineEntry entry, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        if (await store.GetAsync(EngineRecordId(entry), ct).ConfigureAwait(false) is not { Deleted: false } current) return;
        var write = new RecordWrite(current.Id, RecordKinds.Engine, keys.Epoch, Actor, current.Revision, Guid.NewGuid().ToString("n"), true, null);
        if (await store.WriteAsync(write, ct).ConfigureAwait(false) is WriteOutcome.Refused refused) throw new AgentFailure(502, refused.Code, refused.Message);
    }

    /// <summary>
    /// Reads the account's <c>engine</c> records into the local list: the read cache of 9.2. An engine
    /// already known keeps its local pin and tokens.
    /// </summary>
    private async Task RefreshEnginesLockedAsync(CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        long since = 0;
        var found = new List<EngineRecordValue>();
        while (true)
        {
            var page = await store.FeedAsync(since, TimeSpan.Zero, ct).ConfigureAwait(false);
            if (page.Records.Count == 0) break;
            foreach (var record in page.Records.Where(r => r.Kind == RecordKinds.Engine && !r.Deleted && r.Ciphertext is not null && r.Epoch == keys.Epoch))
            {
                try
                {
                    var payload = Constructd.Sync.Sealing.RecordSealer.Open(keys.Records, record.Envelope, record.Ciphertext!);
                    if (Constructd.Sync.Sealing.RecordPayload.ValueOf(payload).Deserialize<EngineRecordValue>(StoreJson.Options) is { } value &&
                        record.Id == "engine:" + EngineEntry.IdFor(value.Url))
                    {
                        found.Add(value);
                    }
                }
                catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException)
                {
                    // Not readable under D: skipped.
                }
            }

            since = page.Cursor;
        }

        lock (_state)
        {
            foreach (var value in found.Where(v => v.Status == "trusted"))
            {
                if (Find(EngineEntry.IdFor(value.Url)) is { } known)
                {
                    known.EngineId ??= value.EngineId;
                    known.HostPublicKey ??= value.HostPublicKey;
                    continue;
                }

                _state.Engines.Add(new EngineEntry
                {
                    Id = EngineEntry.IdFor(value.Url), Url = value.Url, Pin = value.Pin, Name = value.Name,
                    EngineId = value.EngineId, HostPublicKey = value.HostPublicKey,
                });
            }

            Save();
        }
    }

    private async Task WriteRecordAsync(string id, string kind, System.Text.Json.Nodes.JsonNode? value, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await store.GetAsync(id, ct).ConfigureAwait(false);
            var baseRevision = current?.Revision ?? 0;
            var envelope = new RecordEnvelope(id, kind, keys.Epoch, Actor, baseRevision, Guid.NewGuid().ToString("n"), false);
            var payload = Constructd.Sync.Sealing.RecordPayload.Rewrite(null, value, [Actor]);
            var write = new RecordWrite(id, kind, keys.Epoch, Actor, baseRevision, envelope.WriteId, false,
                Constructd.Sync.Sealing.RecordSealer.Seal(keys.Records, envelope, payload));
            switch (await store.WriteAsync(write, ct).ConfigureAwait(false))
            {
                case WriteOutcome.Accepted:
                    return;
                case WriteOutcome.Refused refused:
                    throw new AgentFailure(502, refused.Code, $"The store refused the {kind} record: {refused.Message}");
            }

            // 412: re-read and write the one change again (9.2: a device never merges).
        }

        throw new AgentFailure(409, "write-conflict", $"The {kind} record kept changing; try again.");
    }

    // ---- keys ----

    /// <summary>The account's keys, opened with <c>U</c> from this device's seal and the store's wraps.</summary>
    private async Task<AccountKeys> OpenKeysAsync(CancellationToken ct)
    {
        if (_keys is not null) return _keys;
        RequireRegistered();
        var root = AccountKeys.OpenRoot(_key, _state.RootSeal!);
        try
        {
            using var http = StoreHttp();
            using var response = await http.GetAsync("keys", ct).ConfigureAwait(false);
            await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            var wrapped = await response.Content.ReadFromJsonAsync<WrappedKeys>(StoreJson.Options, ct).ConfigureAwait(false)
                ?? throw new AgentFailure(502, "store-failed", "The store returned no keys.");
            return _keys = AccountKeys.Open(root, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }
    }

    // ---- plumbing ----

    private EngineEntry? Find(string id)
    {
        lock (_state) return _state.Engines.FirstOrDefault(e => e.Id == id);
    }

    private EngineEntry StoreEngine() =>
        (_state.StoreEngine is { } id ? Find(id) : null) ?? throw new AgentFailure(409, "no-store", "This device has no store yet.");

    private static string Normalized(Uri url) => url.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private static AgentFailure NoEngine() => new(404, "no-engine", "No such engine.");

    private void RequireRegistered()
    {
        if (!_state.Registered) throw new AgentFailure(409, "not-registered", "This device is not set up yet.");
    }

    private void RequireUnregistered()
    {
        if (_state.Registered) throw new AgentFailure(409, "already-registered", "This device is set up already.");
    }

    private string Hide(string secret) => Convert.ToBase64String(_protection.Protect(Encoding.UTF8.GetBytes(secret)));

    private string? Reveal(string? hidden) => hidden is null ? null : Encoding.UTF8.GetString(_protection.Unprotect(Convert.FromBase64String(hidden)));

    private void Save()
    {
        lock (_state) StateFile.Save(_options.StatePath, _state);
    }

    private void Changed()
    {
        Events.Publish("agent");
        Streams.Sync();
    }

    /// <summary>The pinned connection to an engine, without credentials (one per engine, reused).</summary>
    private HttpClient Client(EngineEntry entry) =>
        _clients.GetOrAdd(entry.Id, _ => EngineHttp.Client(new Uri(entry.Url), entry.Pin, Timeout.InfiniteTimeSpan));

    private void Forget(string id)
    {
        if (_clients.TryRemove(id, out var client)) client.Dispose();
    }

    /// <summary>Adds this device's credential at <paramref name="entry"/>: its token and a fresh proof, else the user's sign-in.</summary>
    public bool Stamp(string engineId, HttpRequestHeaders headers)
    {
        if (Find(engineId) is not { } entry) return false;
        if (Reveal(entry.DeviceToken) is { } token)
        {
            DeviceCredentialHandler.Stamp(headers, _key, RequestProof.DeviceTokenHeader, token, Time.GetUtcNow());
        }
        else if (Reveal(entry.UserToken) is { } user)
        {
            headers.Authorization = new AuthenticationHeaderValue("Bearer", user);
        }

        return true;
    }

    /// <summary>The pinned client for an engine, for the proxy (credentials are stamped per request).</summary>
    public HttpClient? ProxyClient(string engineId) => Find(engineId) is { } entry ? Client(entry) : null;

    /// <summary>A pinned client to an engine that stamps this device's credential on every request.</summary>
    private HttpClient? EngineClientFor(string engineId) =>
        Find(engineId) is { } entry ? new HttpClient(new StampingHandler(this, engineId) { InnerHandler = EngineHttp.Pinned(entry.Pin) }) { BaseAddress = new Uri(entry.Url), Timeout = Timeout.InfiniteTimeSpan } : null;

    private ConstructdClient EngineApi(EngineEntry entry) =>
        new(new HttpClient(new StampingHandler(this, entry.Id) { InnerHandler = EngineHttp.Pinned(entry.Pin) }) { BaseAddress = new Uri(entry.Url), Timeout = TimeSpan.FromSeconds(30) });

    /// <summary>The store with this device's store token and proof.</summary>
    private HttpClient StoreHttp()
    {
        var engine = StoreEngine();
        return EngineHttp.Client(new Uri(new Uri(engine.Url), "api/v1/store/"), engine.Pin, null,
            new DeviceCredentialHandler(_key, RequestProof.StoreTokenHeader, Reveal(_state.StoreToken)!, Time));
    }

    /// <summary>The store of <paramref name="engine"/> without credentials.</summary>
    private static HttpClient AnonymousStore(EngineEntry engine) => EngineHttp.Client(new Uri(new Uri(engine.Url), "api/v1/store/"), engine.Pin);

    private static async Task EnsureStoreAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string code = "store-failed", message = $"The store answered {(int)response.StatusCode}.";
        try
        {
            if (await response.Content.ReadFromJsonAsync<StoreError>(StoreJson.Options, ct).ConfigureAwait(false) is { Code: { } c } body)
                (code, message) = (c, body.Message ?? message);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        // A refusal by the store is not this API refusing the caller's token: 401 reads as 403 here.
        var status = (int)response.StatusCode switch { 401 => 403, >= 400 and < 500 and var s => s, _ => 502 };
        throw new AgentFailure(status, code, message);
    }

    private sealed record StoreError(string? Code, string? Message);

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (ApiException ex) { throw Failure(ex); }
        catch (HttpRequestException ex) { throw new AgentFailure(502, "engine-unreachable", ex.Message); }
    }

    private static async Task Call(Func<Task> call)
    {
        try { await call().ConfigureAwait(false); }
        catch (ApiException ex) { throw Failure(ex); }
        catch (HttpRequestException ex) { throw new AgentFailure(502, "engine-unreachable", ex.Message); }
    }

    private static AgentFailure Failure(ApiException ex) =>
        new(ex.StatusCode switch { 401 => 403, >= 400 and < 500 and var s => s, _ => 502 }, "engine-refused", $"The engine answered {ex.StatusCode}.");

    public void Dispose()
    {
        _stopping.Cancel();
        foreach (var client in _clients.Values) client.Dispose();
        _keys?.Dispose();
        _key.Dispose();
    }

    private sealed class StampingHandler(DeviceAgent agent, string engineId) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            agent.Stamp(engineId, request.Headers);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>This device as the UI sees it; no key and no token.</summary>
public sealed record DeviceView(string Id, string? Name, string? Account, bool Registered, string? StoreEngine, string Protection, bool ProtectionWeak);

/// <summary>An engine as the UI sees it; whether a credential exists, never the credential.</summary>
public sealed record EngineView(string Id, string Name, string Url, string Pin, string? EngineId, bool Enrolled, bool SignedIn, bool Store);

/// <summary>What an engine presented when probed: the pin for the user to confirm.</summary>
public sealed record EngineProbe(string Url, string Pin, string? Status, string? Known);
