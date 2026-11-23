using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Api.Composition;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Store.Records;
using Constructd.Sync.Vault;
using Constructd.Tests.Store;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Vault;

/// <summary>
/// An account in the new stack (plan 9.1): user "bob" with his primary VM "dev", and his first device "laptop"
/// that set up the account's keys in the engine's built-in store, granted this engine <c>D</c> (and <c>K</c> when
/// the vault is <c>available</c>) and holds a device token here. The engine's record writes go through
/// <see cref="Racing"/>, so a test can make a device write first.
/// </summary>
internal sealed class RecordVaultHarness : IDisposable
{
    public static readonly Uri StoreBase = new("http://localhost/api/v1/store/");
    public static readonly Uri EngineBase = new("http://localhost/api/v1/");

    private RecordVaultHarness(TestApp app, HttpClient bob, HttpClient guest, TestDevice laptop, string engineId, ParticipantPublicKey hostKey)
    {
        App = app; Bob = bob; Guest = guest; Laptop = laptop; EngineId = engineId; HostKey = hostKey;
    }

    public TestApp App { get; }
    public HttpClient Bob { get; }
    public HttpClient Guest { get; }
    public TestDevice Laptop { get; }
    public string EngineId { get; }
    public ParticipantPublicKey HostKey { get; }
    public VaultHostService Vault => App.Service<VaultHostService>();
    public RacingRecords Racing => (RacingRecords)App.Service<IVaultRecords>();
    public AccountKeys Keys => Laptop.Keys!;

    /// <summary>The laptop at the engine, with its device token.</summary>
    public HttpClient Device() => Laptop.Engine(() => App.Server.CreateHandler(), EngineBase, EngineId);

    public static async Task<RecordVaultHarness> CreateAsync(string mode = "available", TestApp? app = null)
    {
        app ??= new TestApp(configureServices: RacingRecords.Register);
        var bob = await app.CreateUserClientAsync("bob");
        var token = (await bob.CreateVmAsync("dev")).VmToken();
        var identity = (await bob.GetFromJsonAsync<EngineIdentityResponse>("/api/v1/engine"))!;
        var laptop = new TestDevice("laptop", () => app.Server.CreateHandler(), StoreBase);
        using (var signedIn = await app.CreateTokenClientAsync("bob"))
        {
            signedIn.BaseAddress = StoreBase;
            Assert.Equal(HttpStatusCode.OK, (await laptop.EnrollAsync(signedIn)).StatusCode);
        }

        await laptop.CreateKeysAsync();
        var hostKey = ParticipantPublicKey.Parse(identity.HostPublicKey);
        var grants = EngineGrants.For(laptop.Keys!, hostKey, identity.EngineId, "bob", mode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PutAsJsonAsync("/api/v1/engine/grants", new EngineGrantsRequest(grants))).StatusCode);
        using (var anonymous = new HttpClient(app.Server.CreateHandler()) { BaseAddress = EngineBase })
        {
            var enrolled = await anonymous.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest("bob", laptop.Id, laptop.EngineEnrollProof(identity.EngineId)));
            Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
            laptop.EngineTokens[identity.EngineId] = (await enrolled.Content.ReadFromJsonAsync<DeviceEnrollResponse>())!.Token;
        }

        return new RecordVaultHarness(app, bob, app.CreateVmTokenClient(token), laptop, identity.EngineId, hostKey);
    }

    public long Ms(TimeSpan from = default) => (App.Clock.UtcNow + from).ToUnixTimeMilliseconds();

    // ---- the guest ----

    public JsonObject Request(string op, string[] names, object? extra = null)
    {
        var request = new JsonObject
        {
            ["v"] = 1, ["op"] = op, ["names"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
            ["deadline"] = Ms(TimeSpan.FromMinutes(10)), ["source"] = "root@dev",
        };
        if (extra is not null)
            foreach (var (key, value) in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject()) request[key] = value?.DeepClone();
        return request;
    }

    public async Task<(HttpStatusCode Status, JsonObject Body)> AskAsync(JsonObject request, HttpClient? client = null, string vm = "dev")
    {
        using var response = await (client ?? Guest).PostAsJsonAsync($"/api/v1/vms/{vm}/vault/requests", request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject());
    }

    public async Task<JsonObject> AnswerAsync(JsonObject request)
    {
        var (status, body) = await AskAsync(request);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body.ToJsonString()}");
        return body;
    }

    public async Task<string> PendingAsync(JsonObject request)
    {
        var (status, body) = await AskAsync(request);
        Assert.True(status == HttpStatusCode.Accepted, $"{status}: {body.ToJsonString()}");
        return body["id"]!.GetValue<string>();
    }

    public async Task<JsonObject> PollAsync(string id)
    {
        using var response = await Guest.GetAsync($"/api/v1/vms/dev/vault/requests/{id}?wait=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>Asks, has the laptop approve, and picks the answer up.</summary>
    public async Task<(string Id, JsonObject Answer)> ApprovedAsync(JsonObject request)
    {
        var id = await PendingAsync(request);
        using var device = Device();
        Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsJsonAsync($"vault/approvals/{id}", new { decision = "approve" })).StatusCode);
        return (id, await PollAsync(id));
    }

    public JsonObject Add(string name, string secret, string description = "a secret", bool? autoApprove = null) =>
        Request("add", [name], new { secret = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)), description, autoApprove });

    // ---- the store, as the laptop reads and writes it ----

    public async Task<StoredRecord?> StoredAsync(string name)
    {
        using var store = Laptop.Store();
        using var response = await store.GetAsync("records/" + Uri.EscapeDataString(SecretRecords.IdOf(name)));
        return response.StatusCode == HttpStatusCode.NotFound ? null : (await response.Content.ReadFromJsonAsync<StoredRecord>(StoreJson.Options))!;
    }

    public async Task<SecretRecord?> ReadAsync(string name) => await StoredAsync(name) is { } stored ? SecretRecords.Read(Keys.Records, stored) : null;

    /// <summary>The value and sealed flag of a live record, opened with <c>K</c>.</summary>
    public async Task<(string Username, string Secret, bool AutoApprove)> OpenAsync(string name)
    {
        var record = (await ReadAsync(name))!;
        return VaultSeal.OpenEntry(Keys.Vault, name, record.Value.UpdatedAt, record.Value.Payload!);
    }

    /// <summary>The laptop writes a secret itself, as the agent does; a tombstone with <paramref name="delete"/>.</summary>
    public async Task<StoredRecord> DeviceWriteAsync(string name, string secret, string description = "set on the laptop", bool autoApprove = false, bool delete = false)
    {
        var stored = await StoredAsync(name);
        var current = stored is null ? null : SecretRecords.Read(Keys.Records, stored);
        var stamp = Math.Max(Ms(), (current?.Value.UpdatedAt ?? 0) + 1);
        var payload = delete ? null : VaultSeal.SealEntry(Keys.Vault, name, stamp, "", secret, autoApprove);
        var value = new SecretRecordValue(name, delete ? "" : description, false, !delete && autoApprove, payload, stamp, Laptop.Actor,
            new SecretProvenance(SecretProvenance.ByDevice, Device: Laptop.Actor));
        var write = SecretRecords.Write(Keys.Records, Keys.Epoch, Laptop.Actor, value, stored?.Revision ?? 0, delete, [Laptop.Actor], current?.Payload);
        using var store = Laptop.Store();
        using var request = new HttpRequestMessage(delete ? HttpMethod.Delete : HttpMethod.Put, "records/" + Uri.EscapeDataString(write.Id))
        {
            Content = JsonContent.Create(write, options: StoreJson.Options),
        };
        request.Headers.TryAddWithoutValidation("If-Match", write.BaseRevision.ToString(CultureInfo.InvariantCulture));
        using var response = await store.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StoredRecord>(StoreJson.Options))!;
    }

    /// <summary><c>K</c> sealed to this engine's host key, as the agent sends it.</summary>
    public object KeyGrant(byte[]? key = null)
    {
        var grant = EngineGrants.Grant(HostKey, EngineId, "bob", EngineGrants.Vault, Keys.Epoch, key ?? Keys.Vault);
        return new { epoch = grant.Epoch, @sealed = grant.Sealed };
    }

    public async Task<string[]> ActivityAsync() =>
        (await Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/activity"))!["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>()).ToArray();

    public async Task<JsonArray> LeasesAsync() => (await Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/leases"))!["leases"]!.AsArray();

    public void Dispose()
    {
        Guest.Dispose(); Bob.Dispose(); Laptop.Dispose(); App.Dispose();
    }
}

/// <summary>The engine's record vault, with a hook that runs once before its next write (a device that wrote first).</summary>
internal sealed class RacingRecords(IVaultRecords inner) : IVaultRecords
{
    public Func<Task>? BeforeNextWrite { get; set; }

    public int Writes { get; private set; }

    public int Stale { get; private set; }

    public static void Register(IServiceCollection services) =>
        services.AddSingleton<IVaultRecords>(sp => new RacingRecords(ActivatorUtilities.CreateInstance<RecordVault>(sp)));

    public bool Hosts(string owner) => inner.Hosts(owner);
    public IReadOnlyList<string> Owners() => inner.Owners();
    public byte[]? GrantedKey(string owner) => inner.GrantedKey(owner);
    public byte[] OpenKey(string owner, VaultKeyGrant grant) => inner.OpenKey(owner, grant);
    public void SetMode(string owner, bool available, VaultKeyGrant? grant) => inner.SetMode(owner, available, grant);
    public Task<IReadOnlyList<VaultEntry>> ListAsync(string owner, CancellationToken ct) => inner.ListAsync(owner, ct);

    public async Task<bool> WriteAsync(string owner, VaultEntry entry, long baseRevision, VaultProvenance provenance, IReadOnlyList<string> origins, CancellationToken ct)
    {
        if (BeforeNextWrite is { } hook)
        {
            BeforeNextWrite = null;
            await hook();
        }

        Writes++;
        var accepted = await inner.WriteAsync(owner, entry, baseRevision, provenance, origins, ct);
        if (!accepted) Stale++;
        return accepted;
    }
}
