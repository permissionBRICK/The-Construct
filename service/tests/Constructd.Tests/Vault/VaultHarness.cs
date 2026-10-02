using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Api.Auth;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Tests.Support;

namespace Constructd.Tests.Vault;

/// <summary>
/// A host with user "bob", his primary VM "dev" (created through the API, so the fake driver runs it)
/// and the VM's guest client, plus bob's vault key K. Settings are applied unless asked otherwise.
/// </summary>
internal sealed class VaultHarness : IDisposable
{
    private VaultHarness(TestApp app, HttpClient bob, HttpClient guest, string guestToken)
    {
        App = app; Bob = bob; Guest = guest; GuestToken = guestToken;
    }

    public TestApp App { get; }
    public HttpClient Bob { get; }
    public HttpClient Guest { get; }
    public string GuestToken { get; }
    public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    public string KeyBase64 => Convert.ToBase64String(Key);
    public VaultHostService Vault => App.Service<VaultHostService>();

    public static async Task<VaultHarness> CreateAsync(string? mode = "available", TestApp? app = null)
    {
        app ??= new TestApp();
        var bob = await app.CreateUserClientAsync("bob");
        var job = await bob.CreateVmAsync("dev");
        var token = job.VmToken();
        var harness = new VaultHarness(app, bob, app.CreateVmTokenClient(token), token);
        if (mode is not null) await harness.SettingsAsync(mode);
        return harness;
    }

    public async Task SettingsAsync(string mode)
    {
        using var response = await Bob.PutAsJsonAsync("/api/v1/vault/settings",
            new { mode, key = mode == "available" ? KeyBase64 : null, keyCheck = VaultCrypto.KeyCheck(Key) });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public long Ms(TimeSpan from = default) => (App.Clock.UtcNow + from).ToUnixTimeMilliseconds();

    public object Entry(string name, string secret, string username = "", string description = "", long? at = null, string by = "pc:test")
    {
        var stamp = at ?? Ms();
        return new { name, description, hasUsername = username.Length > 0, payload = VaultCrypto.SealEntry(Key, name, stamp, username, secret), updatedAt = stamp, updatedBy = by, deleted = false };
    }

    public async Task<JsonObject> PutEntriesAsync(params object[] entries)
    {
        using var response = await Bob.PutAsJsonAsync("/api/v1/vault/entries", new { entries });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    public async Task<JsonObject> EntriesAsync() => (await Bob.GetFromJsonAsync<JsonObject>("/api/v1/vault/entries"))!;

    /// <summary>Posts a guest request; returns the status and the body (a response document or {id}).</summary>
    public async Task<(HttpStatusCode Status, JsonObject Body)> AskAsync(object request, HttpClient? client = null, string vm = "dev")
    {
        using var response = await (client ?? Guest).PostAsJsonAsync($"/api/v1/vms/{vm}/vault/requests", request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject());
    }

    /// <summary>Asks and expects an immediate response document.</summary>
    public async Task<JsonObject> AnswerAsync(object request)
    {
        var (status, body) = await AskAsync(request);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body.ToJsonString()}");
        return body;
    }

    /// <summary>Asks and expects a pending approval; returns its id.</summary>
    public async Task<string> PendingAsync(object request)
    {
        var (status, body) = await AskAsync(request);
        Assert.True(status == HttpStatusCode.Accepted, $"{status}: {body.ToJsonString()}");
        var id = body["id"]!.GetValue<string>();
        // The guest's pending note (T3 Code's banner) links straight to this request on the approval page.
        Assert.Matches(@"^https?://[^/]+/vault/#request=" + id + "$", body["approveUrl"]!.GetValue<string>());
        return id;
    }

    public async Task<(HttpStatusCode Status, JsonObject? Body)> PollAsync(string id, int wait = 0)
    {
        using var response = await Guest.GetAsync($"/api/v1/vms/dev/vault/requests/{id}?wait={wait}");
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text)!.AsObject());
    }

    public async Task<HttpStatusCode> DecideAsync(string id, string decision, HttpClient? client = null)
    {
        using var response = await (client ?? Bob).PostAsJsonAsync($"/api/v1/vault/approvals/{id}", new { decision });
        return response.StatusCode;
    }

    /// <summary>Asks, approves as bob and picks the answer up.</summary>
    public async Task<JsonObject> ApprovedAsync(object request)
    {
        var id = await PendingAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, await DecideAsync(id, "approve"));
        var (status, body) = await PollAsync(id);
        Assert.Equal(HttpStatusCode.OK, status);
        return body!;
    }

    public object Request(string op, string[] names, object? extra = null)
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

    public static string Secret(JsonObject response) => Encoding.UTF8.GetString(Convert.FromBase64String(response["secret"]!.GetValue<string>()));

    public HttpClient DeviceClient(string token)
    {
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ConstructdSchemes.VaultDevice, token);
        return client;
    }

    public async Task<JsonObject> HeartbeatAsync()
    {
        using var response = await Guest.PostAsJsonAsync("/api/v1/vms/dev/activity", new { busy = false, reasons = Array.Empty<string>() });
        if (response.StatusCode == HttpStatusCode.NoContent) return [];
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    public void Dispose()
    {
        Guest.Dispose(); Bob.Dispose(); App.Dispose();
        CryptographicOperations.ZeroMemory(Key);
    }
}
