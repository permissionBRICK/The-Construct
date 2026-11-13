using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Api.Composition;
using Constructd.Api.Contracts;
using Constructd.Api.Jobs;
using Constructd.Api.Profiles;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Sync.Participation;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Store;

/// <summary>
/// Guests whose projects folders are directories here: the engine's SSH scripts run in a real
/// <c>bash</c> with <c>CONSTRUCT_PROJECTS_STORE</c> pointing at the VM's directory, so the read and
/// the guarded write are the ones a VM runs.
/// </summary>
public sealed class FolderGuests : ISshRunner, IProvisionTargets, IDisposable
{
    private readonly TempDirectory _root = new();

    public List<string> Scripts { get; } = [];

    public Task ForgetHostKeyAsync(SshTarget target, CancellationToken ct) => Task.CompletedTask;

    /// <summary>VMs that do not answer.</summary>
    public HashSet<string> Down { get; } = new(StringComparer.Ordinal);

    public string Folder(string vm) => Path.Combine(_root.Path, vm);

    /// <summary>The VM's root <c>authorized_keys</c> (beside its folder).</summary>
    public string AuthorizedKeysPath(string vm) => Path.Combine(_root.Path, vm + ".ssh", "authorized_keys");

    public string AuthorizedKeys(string vm) => System.IO.File.Exists(AuthorizedKeysPath(vm)) ? System.IO.File.ReadAllText(AuthorizedKeysPath(vm)) : "";

    public string? File(string vm, string profile) =>
        System.IO.File.Exists(Path.Combine(Folder(vm), profile + ".json")) ? System.IO.File.ReadAllText(Path.Combine(Folder(vm), profile + ".json")) : null;

    public long? Rev(string vm, string profile)
    {
        var path = Path.Combine(Folder(vm), ".state", profile + ".rev");
        return System.IO.File.Exists(path) ? long.Parse(System.IO.File.ReadAllText(path).Trim(), System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public JsonObject? Profile(string vm, string profile) => File(vm, profile) is { } text ? JsonNode.Parse(text)!.AsObject() : null;

    /// <summary>A direct edit in the VM: the agent writes the file itself.</summary>
    public void Edit(string vm, string profile, string json)
    {
        Directory.CreateDirectory(Folder(vm));
        System.IO.File.WriteAllText(Path.Combine(Folder(vm), profile + ".json"), json);
    }

    /// <summary>What <c>construct project set|delete|resolve</c> does with the engine's answer.</summary>
    public void Apply(string vm, string profile, ProfileWriteResponse answer)
    {
        Directory.CreateDirectory(Path.Combine(Folder(vm), ".state"));
        var file = Path.Combine(Folder(vm), profile + ".json");
        if (answer.File is null) System.IO.File.Delete(file);
        else System.IO.File.WriteAllText(file, answer.File);
        if (answer.Revision is { } revision) System.IO.File.WriteAllText(Path.Combine(Folder(vm), ".state", profile + ".rev"), revision + "\n");
        var marker = Path.Combine(Folder(vm), ".deleted", profile);
        if (System.IO.File.Exists(marker)) System.IO.File.Delete(marker);
    }

    /// <summary>The heartbeat's digest of a VM's folder (any value that changes with it will do here).</summary>
    public string Digest(string vm)
    {
        var builder = new StringBuilder();
        if (Directory.Exists(Folder(vm)))
        {
            foreach (var file in Directory.EnumerateFiles(Folder(vm), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                builder.Append(file).Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(file))));
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public Task<SshTarget?> ResolveAsync(Vm vm, CancellationToken ct) => Task.FromResult<SshTarget?>(new SshTarget(vm.Name, 22, "root", null, null));

    public string? BootstrapPublicKey() => null;

    public async Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
    {
        Assert.Equal("bash -s", command);
        lock (Scripts) Scripts.Add(standardInput ?? "");
        if (Down.Contains(target.Host)) return 255;
        var start = new ProcessStartInfo("bash", "-s") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.Environment["CONSTRUCT_PROJECTS_STORE"] = Folder(target.Host);
        start.Environment["CONSTRUCT_AUTHORIZED_KEYS"] = AuthorizedKeysPath(target.Host);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(standardInput);
        process.StandardInput.Close();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null) lines?.Report(line);
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public void Dispose() => _root.Dispose();
}

/// <summary>The stores of other places, for an engine configured with a store URL: an in-process server.</summary>
public sealed class InProcessStoreHandler : StoreHttpHandler
{
    public Func<HttpMessageHandler>? Server { get; set; }

    /// <summary>While set, the store cannot be reached.</summary>
    public bool Down { get; set; }

    public override HttpMessageHandler Create() => new Gate(this);

    private sealed class Gate(InProcessStoreHandler owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (owner.Down || owner.Server is null) throw new HttpRequestException("The store is down.");
            using var invoker = new HttpMessageInvoker(owner.Server(), disposeHandler: false);
            return invoker.SendAsync(request, ct);
        }
    }
}

/// <summary>An engine in fake mode with folder guests, a user and, unless told otherwise, a device that granted it <c>D</c>.</summary>
public sealed class ProfileSyncHarness : IAsyncDisposable
{
    public const string User = "alice";
    public static readonly Uri StoreBase = new("http://localhost/api/v1/store/");
    public static readonly Uri EngineBase = new("http://localhost/api/v1/");

    public TestApp App { get; }
    public FolderGuests Guests { get; }
    public InProcessStoreHandler RemoteStores { get; } = new();
    public HttpClient Alice { get; private set; } = null!;
    public TestDevice? Laptop { get; private set; }
    public Dictionary<string, string> VmTokens { get; } = new(StringComparer.Ordinal);

    private ProfileSyncHarness(IDictionary<string, string?>? settings, FolderGuests guests)
    {
        Guests = guests;
        App = new TestApp(settings, services =>
        {
            services.AddSingleton<ISshRunner>(guests);
            services.AddSingleton<IProvisionTargets>(guests);
            services.AddSingleton<StoreHttpHandler>(RemoteStores);
        });
    }

    public string EngineId => App.Service<EngineStoreIdentity>().Id;

    public ProfileSync Sync => App.Service<ProfileSync>();

    /// <summary>An engine with its own store: alice's first device enrols there, creates the keys, grants the engine D and enrols at the engine.</summary>
    public static async Task<ProfileSyncHarness> StartAsync()
    {
        var harness = new ProfileSyncHarness(null, new FolderGuests());
        harness.Alice = await harness.App.CreateUserClientAsync(User);
        var laptop = new TestDevice("laptop", () => harness.App.Server.CreateHandler(), StoreBase);
        using (var signedIn = await harness.App.CreateTokenClientAsync(User))
        {
            signedIn.BaseAddress = StoreBase;
            Assert.Equal(HttpStatusCode.OK, (await laptop.EnrollAsync(signedIn)).StatusCode);
        }

        await laptop.CreateKeysAsync();
        harness.Grant(laptop.Keys!);
        laptop.EngineTokens[harness.EngineId] = await harness.EnrollAtEngineAsync(laptop);
        harness.Laptop = laptop;
        return harness;
    }

    /// <summary>An engine whose account syncs against a store elsewhere (<see cref="RemoteStores"/>), with the given participant token.</summary>
    public static async Task<ProfileSyncHarness> StartRemoteAsync(string token)
    {
        var harness = new ProfileSyncHarness(new Dictionary<string, string?>
        {
            ["Constructd:Sync:Stores:0:Account"] = User,
            ["Constructd:Sync:Stores:0:Url"] = "http://localhost",
            ["Constructd:Sync:Stores:0:Token"] = token,
        }, new FolderGuests());
        harness.Alice = await harness.App.CreateUserClientAsync(User);
        return harness;
    }

    public void Grant(AccountKeys keys)
    {
        var engine = App.Service<EngineKeyStore>();
        foreach (var grant in EngineGrants.For(keys, engine.HostKey.PublicKey, EngineId, User, "locked")) engine.PutGrant(User, grant);
    }

    public async Task<string> EnrollAtEngineAsync(TestDevice device)
    {
        using var anonymous = new HttpClient(App.Server.CreateHandler()) { BaseAddress = EngineBase };
        var response = await anonymous.PostAsJsonAsync("devices/enroll", new DeviceEnrollRequest(device.Account!, device.Id, device.EngineEnrollProof(EngineId)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeviceEnrollResponse>())!.Token;
    }

    public HttpClient DeviceClient(TestDevice device) => device.Engine(() => App.Server.CreateHandler(), EngineBase, EngineId);

    /// <summary>Creates a running VM of alice that selects <paramref name="profiles"/>.</summary>
    public async Task<HttpClient> VmAsync(string name, params string[] profiles)
    {
        var job = await Alice.CreateVmAsync(name);
        VmTokens[name] = job.VmToken();
        Directory.CreateDirectory(Guests.Folder(name));
        if (profiles.Length > 0) await SelectAsync(name, profiles);
        return App.CreateVmTokenClient(VmTokens[name]);
    }

    public Task SelectAsync(string vm, params string[] profiles) =>
        App.Service<IVmDesiredStateSource>().SetDesiredAsync(vm,
            new StoredDesiredState(new VmDesiredState(profiles, new ProvisionSettings()), new string('a', 64), DateTimeOffset.UtcNow, User), default);

    public Task SyncAsync() => Sync.SyncAsync(default);

    /// <summary>A VM's heartbeat with its folder's digest; the reply's conflicts.</summary>
    public async Task<JsonArray> HeartbeatAsync(HttpClient vm, string name)
    {
        var response = await vm.PostAsJsonAsync($"/api/v1/vms/{name}/activity", new { busy = false, reasons = Array.Empty<string>(), projectsDigest = Guests.Digest(name) });
        Assert.True(response.IsSuccessStatusCode, $"heartbeat: {response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.NoContent) return [];
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["conflicts"] as JsonArray ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        Laptop?.Dispose();
        Alice?.Dispose();
        await App.DisposeAsync();
        Guests.Dispose();
    }
}

public static class ProfileSyncApi
{
    public static async Task<ProfileWriteResponse> PutProfileAsync(this HttpClient client, string path, string json, long? baseRevision)
    {
        var response = await client.PutAsJsonAsync(path, new ProfileWriteRequest(JsonNode.Parse(json)!.AsObject(), baseRevision));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"PUT {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ProfileWriteResponse>())!;
    }

    public static async Task<ProfileWriteResponse> DeleteProfileAsync(this HttpClient client, string path, long? baseRevision)
    {
        var response = await client.DeleteAsync(path + (baseRevision is { } b ? "?baseRevision=" + b : ""));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"DELETE {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ProfileWriteResponse>())!;
    }

    public static async Task<ProfileWriteResponse> ResolveAsync(this HttpClient client, string path, string json)
    {
        var response = await client.PostAsJsonAsync(path, new ProfileResolveRequest(JsonNode.Parse(json)!.AsObject()));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ProfileWriteResponse>())!;
    }
}
