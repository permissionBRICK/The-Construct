using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Api.Backups;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Backups;

/// <summary>A free-space probe the test sets, call by call when it needs to.</summary>
internal sealed class FakeFreeSpaceProbe : IFreeSpaceProbe
{
    private int _calls;
    public long Free { get; set; } = 100L << 30;
    /// <summary>When set, the answer for the n-th call (1-based).</summary>
    public Func<int, long>? ByCall { get; set; }
    public int Calls => _calls;
    public long FreeBytes(string path)
    {
        var call = Interlocked.Increment(ref _calls);
        return ByCall?.Invoke(call) ?? Free;
    }
}

/// <summary>A body without a length, so it reaches the service chunked.</summary>
internal sealed class UnsizedStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 64 * 1024));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A host with user "alice" and her primary VM "dev" (created through the API), the VM's guest client,
/// another user "bob" with his own primary "other", an admin, a temporary backup directory and a fake
/// free-space probe.
/// </summary>
internal sealed class BackupHarness : IDisposable
{
    public required TestApp App { get; init; }
    public required HttpClient Alice { get; init; }
    public required HttpClient Bob { get; init; }
    public required HttpClient Admin { get; init; }
    public required HttpClient Guest { get; init; }
    public required HttpClient OtherGuest { get; init; }
    public required string Root { get; init; }
    public required string? Database { get; init; }
    public FakeFreeSpaceProbe Probe => (FakeFreeSpaceProbe)App.Service<IFreeSpaceProbe>();
    public BackupCoordinator Coordinator => App.Service<BackupCoordinator>();
    public IBackupStore Store => App.Service<IBackupStore>();

    public static async Task<BackupHarness> CreateAsync(bool sqlite = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "constructd-backup-test-" + Guid.NewGuid().ToString("n"));
        var database = sqlite ? Path.Combine(root + "-db", "constructd.db") : null;
        var settings = new Dictionary<string, string?> { ["Constructd:Backups:RootDir"] = root };
        void Probe(IServiceCollection services) => services.AddSingleton<IFreeSpaceProbe>(new FakeFreeSpaceProbe());
        var app = sqlite ? TestApp.WithSqlite(database!, settings, Probe) : new TestApp(settings, Probe);
        app.Clock.UtcNow = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var alice = await app.CreateUserClientAsync("alice");
        var bob = await app.CreateUserClientAsync("bob");
        var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var guest = app.CreateVmTokenClient((await alice.CreateVmAsync("dev")).VmToken());
        var other = app.CreateVmTokenClient((await bob.CreateVmAsync("other")).VmToken());
        return new BackupHarness
        {
            App = app, Alice = alice, Bob = bob, Admin = admin, Guest = guest, OtherGuest = other, Root = root, Database = database,
        };
    }

    public static string Keys(string id, int recipients = 1) => new JsonObject
    {
        ["format"] = "construct-backup-keys/1", ["backupId"] = id, ["createdAt"] = "2026-11-04T10:00:00Z",
        ["recipients"] = new JsonArray(Enumerable.Range(0, recipients).Select(i => (JsonNode)new JsonObject
        {
            ["fingerprint"] = Convert.ToHexStringLower(SHA256.HashData([(byte)i])),
            ["label"] = $"PC {i}",
            ["wrapped"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(384)),
        }).ToArray()),
    }.ToJsonString();

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public async Task<(HttpStatusCode Status, JsonObject Body)> OpenAsync(string trigger = "manual", long estimatedBytes = 1000, HttpClient? client = null)
    {
        using var response = await (client ?? Guest).PostAsJsonAsync("/api/v1/vms/dev/backups", new { trigger, estimatedBytes });
        return (response.StatusCode, await BodyAsync(response));
    }

    public async Task<string> OpenIdAsync(string trigger = "manual", long estimatedBytes = 1000)
    {
        var (status, body) = await OpenAsync(trigger, estimatedBytes);
        Assert.Equal(HttpStatusCode.Created, status);
        return body["id"]!.GetValue<string>();
    }

    public async Task<HttpStatusCode> PutKeysAsync(string id, string? json = null)
    {
        using var response = await Guest.PutAsync($"/api/v1/vms/dev/backups/{id}/keys", Json(json ?? Keys(id)));
        return response.StatusCode;
    }

    public Task<(HttpStatusCode Status, JsonObject Body)> UploadAsync(string id, byte[] payload, bool chunked = false) =>
        UploadAsync(id, chunked ? new StreamContent(new UnsizedStream(payload)) : new ByteArrayContent(payload), chunked);

    public async Task<(HttpStatusCode Status, JsonObject Body)> UploadAsync(string id, HttpContent content, bool chunked)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/vms/dev/backups/{id}/content") { Content = content };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        using var response = await Guest.SendAsync(request);
        return (response.StatusCode, await BodyAsync(response));
    }

    /// <summary>Deletes a VM through the API, the way a reinstall does, and waits for the job.</summary>
    public static async Task DeleteVmAsync(HttpClient owner, string name)
    {
        using var accepted = await owner.DeleteAsync($"/api/v1/vms/{name}");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = await owner.WaitForJobAsync((await accepted.ReadAsync<Constructd.Api.Contracts.JobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, job.State);
    }

    /// <summary>The files under the backup directory (none when it was never created).</summary>
    public string[] Files(string pattern = "*") =>
        Directory.Exists(Root) ? Directory.GetFiles(Root, pattern, SearchOption.AllDirectories) : [];

    /// <summary>The guest abandons its open session.</summary>
    public async Task<HttpStatusCode> AbandonAsync(string id)
    {
        using var response = await Guest.DeleteAsync($"/api/v1/vms/dev/backups/{id}");
        return response.StatusCode;
    }

    /// <summary>Open, keys, payload: a complete backup.</summary>
    public async Task<string> BackupAsync(byte[]? payload = null, string trigger = "manual")
    {
        var id = await OpenIdAsync(trigger);
        Assert.Equal(HttpStatusCode.NoContent, await PutKeysAsync(id));
        var (status, body) = await UploadAsync(id, payload ?? RandomNumberGenerator.GetBytes(4096));
        Assert.Equal(HttpStatusCode.OK, status);
        return body["id"]!.GetValue<string>();
    }

    public async Task<JsonObject> HeartbeatAsync(bool busy = false, HttpClient? client = null, string vm = "dev")
    {
        using var response = await (client ?? Guest).PostAsJsonAsync($"/api/v1/vms/{vm}/activity", new { busy, reasons = Array.Empty<string>() });
        if (response.StatusCode == HttpStatusCode.NoContent) return [];
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // 200 only with something due; nothing due is 204.
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.NotEmpty(body);
        return body;
    }

    public async Task<JsonObject> GetAsync(string path, HttpClient? client = null)
    {
        using var response = await (client ?? Alice).GetAsync("/api/v1/vms/dev/backups" + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    public async Task<JsonArray> ListAsync(HttpClient? client = null) => (await GetAsync("", client))["backups"]!.AsArray();

    public async Task ConfigAsync(object backups)
    {
        using var response = await Admin.PutAsJsonAsync("/api/v1/host/config", new { backups });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static async Task<JsonObject> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? [] : JsonNode.Parse(text) as JsonObject ?? [];
    }

    public static object Config(bool enabled = true, int intervalHours = 24, int keep = 4, int maxKeep = 10, int pinDays = 14,
        int retainDeletedDays = 14, long minFreeBytes = 5L << 30, long? maxBytesPerVm = null) =>
        new { enabled, intervalHours, keep, maxKeep, pinDays, retainDeletedDays, minFreeBytes, maxBytesPerVm };

    public void Dispose()
    {
        foreach (var client in new[] { Alice, Bob, Admin, Guest, OtherGuest }) client.Dispose();
        App.Dispose();
        foreach (var directory in new[] { Root, Database is null ? null : Path.GetDirectoryName(Database) })
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
