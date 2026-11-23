using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// The old client's root key for a VM of a host that ran the old stack and updated itself in place
/// (<c>PUT /vms/{name}/old-client-key</c>, <see cref="OldClientKeys"/>): only the VM's owner hands it over, the engine keeps
/// it only once it reached the VM as root, never replaces a key that works, and never says it back.
/// </summary>
public sealed class OldClientKeyTests : IDisposable
{
    // The engine keeps the key next to its database: each test its own.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "construct-old-client-key-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private const string KeyA = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAAold-client-key-a\n-----END OPENSSH PRIVATE KEY-----\n";
    private const string KeyB = "-----BEGIN OPENSSH PRIVATE KEY-----\nBBBBold-client-key-b\n-----END OPENSSH PRIVATE KEY-----\n";

    private async Task<(TestApp App, Guest Guest, HttpClient Owner)> StartAsync()
    {
        var guest = new Guest();
        var app = new TestApp(new Dictionary<string, string?> { ["Constructd:DatabasePath"] = Path.Combine(_dir, "constructd.db") },
            services => services.AddSingleton<ISshRunner>(guest));
        var owner = await app.CreateUserClientAsync("alice", Role.Admin);
        await app.Vms.AddAsync(new Vm("far", "alice", 2, 4, 50, DateTimeOffset.UtcNow.AddDays(-30), VmState.Running, null, null,
            IdlePolicy.Disabled, Vm.NoForwards, TokenKind: VmTokenKind.Primary), 10, CancellationToken.None);
        app.Driver.SetState("far", VmState.Running);
        return (app, guest, owner);
    }

    private static object Body(string key, object? instanceState = null) => new
    {
        sshPrivateKey = key,
        settings = new { gitUserName = "Ada", gitEmail = "ada@example.test" },
        instanceState = instanceState ?? new { projects = new[] { "web" }, t3code = true },
    };

    private static async Task<string> StatusAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        return JsonDocument.Parse(text).RootElement.GetProperty("status").GetString()!;
    }

    private static string KeyFile(TestApp app) => ProvisionTargets.AdoptedKeyPath(app.Service<ConstructdOptions>(), "far");

    [Fact]
    public async Task Only_the_vms_owner_hands_over_a_key_and_it_is_never_said_back()
    {
        var (app, guest, owner) = await StartAsync();
        using var _ = app;
        guest.RootKeys.Add(KeyA);
        var other = await app.CreateUserClientAsync("bob", Role.Admin);

        // Another user, even an admin, gets no root on the VM through this route; a missing key is refused before any SSH.
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync("/api/v1/vms/nope/old-client-key", Body(KeyA))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", new { sshPrivateKey = "not a key" })).StatusCode);
        Assert.Empty(guest.Calls);
        Assert.False(File.Exists(KeyFile(app)));

        Assert.Equal("stored", await StatusAsync(await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA))));

        var audit = await app.Service<IAuditLog>().QueryAsync(50, CancellationToken.None);
        var entry = Assert.Single(audit, e => e.Action == "vm.old-client-key" && e.Outcome == AuditOutcome.Success);
        Assert.Equal("far", entry.Target);
        Assert.Contains("status=stored", entry.Detail);
        Assert.DoesNotContain(audit, e => (e.Detail ?? "").Contains("PRIVATE KEY", StringComparison.Ordinal) || (e.Detail ?? "").Contains("old-client-key-a", StringComparison.Ordinal));
        Assert.DoesNotContain("old-client-key-a", app.Logs.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_is_kept_only_once_it_reached_the_vm_as_root_and_then_later_jobs_dial_root_with_it()
    {
        // Field run: after the old host updated itself, the engine dialled the seed user, whose bootstrap key the old
        // provisioning had removed; only the old client's root key reaches the guest.
        var (app, guest, owner) = await StartAsync();
        using var _ = app;
        guest.AiTools = "'opencode,claude-code'";

        var refused = await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadAsStringAsync();
        Assert.Contains("key-rejected", problem);
        Assert.DoesNotContain("PRIVATE KEY", problem, StringComparison.Ordinal);
        Assert.False(File.Exists(KeyFile(app)));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(KeyFile(app))!, "far_root*"));
        Assert.Null(await app.Service<IVmDesiredStateSource>().GetDesiredAsync("far", CancellationToken.None));

        guest.RootKeys.Add(KeyA);
        Assert.Equal("stored", await StatusAsync(await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA.Replace("\n", "\r\n")))));
        Assert.Equal(KeyA, File.ReadAllText(KeyFile(app)));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile(app)));
        // The seed user was asked first (the engine keeps what works), then root with the key.
        Assert.Equal(["construct", "root"], guest.Calls.Select(c => c.User).Distinct());

        var vm = (await app.Vms.GetAsync("far", CancellationToken.None))!;
        var target = (await app.Service<IProvisionTargets>().ResolveAsync(vm, CancellationToken.None))!;
        Assert.Equal(("root", KeyFile(app)), (target.User, target.IdentityFile));

        // The old stack stored no settings: the old client's become the VM's, with the coding agents the guest has.
        var desired = (await app.Service<IVmDesiredStateSource>().GetDesiredForAsync(vm, CancellationToken.None))!.Desired;
        Assert.Equal(["web"], desired.Projects);
        Assert.Equal(("Ada", "true"), (desired.Settings.GitUserName, desired.Settings.T3Code));
        Assert.Equal(["opencode", "claude-code"], desired.CodingAgents);
    }

    [Fact]
    public async Task The_same_key_again_changes_nothing_and_a_different_key_never_replaces_one_that_works()
    {
        var (app, guest, owner) = await StartAsync();
        using var _ = app;
        guest.RootKeys.Add(KeyA);
        Assert.Equal("stored", await StatusAsync(await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA))));
        var stored = await app.Service<IVmDesiredStateSource>().GetDesiredAsync("far", CancellationToken.None);

        Assert.Equal("kept", await StatusAsync(await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA))));

        // B reaches the VM too, but A works: A stays, and the stored settings are not replaced by other ones.
        guest.RootKeys.Add(KeyB);
        Assert.Equal("reachable", await StatusAsync(await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key",
            Body(KeyB, new { projects = new[] { "other" } }))));
        Assert.Equal(KeyA, File.ReadAllText(KeyFile(app)));
        var after = await app.Service<IVmDesiredStateSource>().GetDesiredAsync("far", CancellationToken.None);
        Assert.Equal((stored!.Hash, stored.UpdatedAt), (after!.Hash, after.UpdatedAt));

        // A no longer reaches the VM: B, which does, takes its place.
        guest.RootKeys.Remove(KeyA);
        Assert.Equal("stored", await StatusAsync(await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyB))));
        Assert.Equal(KeyB, File.ReadAllText(KeyFile(app)));

        // Neither reaches it: what is kept stays as it is.
        guest.RootKeys.Clear();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA))).StatusCode);
        Assert.Equal(KeyB, File.ReadAllText(KeyFile(app)));
    }

    [Fact]
    public async Task A_vm_the_engine_reaches_as_its_seed_user_keeps_that_way_and_a_stopped_vm_is_named()
    {
        var (app, guest, owner) = await StartAsync();
        using var _ = app;
        guest.SeedUserAnswers = true;
        guest.RootKeys.Add(KeyA);

        Assert.Equal("reachable", await StatusAsync(await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA))));
        Assert.False(File.Exists(KeyFile(app)));

        app.Driver.SetState("far", VmState.Off);
        var stopped = await owner.PutAsJsonAsync("/api/v1/vms/far/old-client-key", Body(KeyA));
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Contains("vm-not-running", await stopped.Content.ReadAsStringAsync());
    }

    /// <summary>A guest whose sshd lets root in with the keys in <see cref="RootKeys"/> only, and the seed user when told.</summary>
    private sealed class Guest : ISshRunner
    {
        public HashSet<string> RootKeys { get; } = [];
        public bool SeedUserAnswers { get; set; }
        public string? AiTools { get; set; }
        public List<SshTarget> Calls { get; } = [];

        public Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls) Calls.Add(target);
            var key = target.IdentityFile is { } file && File.Exists(file) ? File.ReadAllText(file) : null;
            var admitted = target.User == "root" ? key is not null && RootKeys.Contains(key) : SeedUserAnswers;
            // Closed before authentication, as ssh reports it.
            if (!admitted) return Task.FromResult(255);
            if (command == OldClientKeys.ProbeCommand && AiTools is not null) lines?.Report(AiTools);
            return Task.FromResult(0);
        }

        public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken ct) => Task.CompletedTask;
    }
}
