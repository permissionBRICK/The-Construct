using System.Net;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Construct.Agent.Upgrade;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Store.Records;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Agent.Tests;

/// <summary>
/// The one-time upgrade (plan 11.6, 13.1, 13.2) against fixtures in the old client's formats and the real engine in
/// fake mode: everything arrives, a second run changes nothing, a run cut in the middle and repeated ends as an uncut
/// one, and the old files are byte for byte what they were.
/// </summary>
public sealed class UpgradeTests
{
    private static readonly Dictionary<string, string> MainProfiles = new()
    {
        ["projects/web.json"] = """{"name":"web","repos":[{"url":"https://example.test/web.git"}],"provisionCommands":["npm install"]}""",
        ["projects/api.json"] = """{"name":"api","repos":[{"url":"https://example.test/api.git"}]}""",
        ["projects/default.json"] = """{"name":"default"}""",
        ["manifest/remotes.json"] = """{"remotes":[{"url":"https://git.example.test/company/configs.git"}]}""",
        ["manifest/api.json"] = """{"remoteUrl":"https://git.example.test/company/configs.git","ref":"main","pathInRemote":"projects/api.json"}""",
    };

    /// <summary>The default VM's install: one local VM, no registry, a VM branch with an unmerged edit, a vault, a backup key.</summary>
    private static void SingleVm(LegacyFixture f)
    {
        f.Settings(new JsonObject
        {
            ["constructRepo"] = "permissionBRICK/The-Construct", ["constructRef"] = "main", ["installedCommit"] = new string('a', 40),
            ["gitUserName"] = "Ada", ["gitEmail"] = "ada@example.test", ["projects"] = new JsonArray("web", "api"), ["micPassthrough"] = true, ["t3code"] = true,
        });
        f.SshKey("agent_vm_ed25519");
        f.ConfigRepo(MainProfiles, "vm",
            new Dictionary<string, string?> { ["projects/web.json"] = """{"name":"web","repos":[{"url":"https://example.test/web.git"}],"provisionCommands":["pnpm install"]}""",
                ["projects/vmonly.json"] = """{"name":"vmonly","repos":[]}""" },
            new Dictionary<string, string> { ["projects/web.json"] = """{"name":"web","repos":[{"url":"https://example.test/web.git"}],"provisionCommands":["yarn install"]}""" });
        f.Vault([("github", "ghp_Fixture_Secret_1", "octo"), ("auto-npm", "npm_Fixture_Secret_2", null)], leases: 1);
        f.BackupKey();
    }

    [Fact]
    public async Task A_single_local_VM_install_arrives_whole_and_a_second_run_changes_nothing()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        var before = rig.Fixture.Digests();

        var found = await rig.Agent.GetAsync("api/upgrade");
        Assert.True(found["legacy"]!.GetValue<bool>());
        Assert.Equal(["agent-vm"], found["found"]!["localVms"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["api", "web"], found["found"]!["profiles"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(2, found["found"]!["unmergedChanges"]!.GetValue<int>()); // web (both changed) and vmonly (new on the branch)

        await rig.EngineStepAsync();
        // Conversion before the recovery code is refused: the records are sealed under the account's keys.
        await rig.Agent.PostAsync("api/upgrade/convert", null, HttpStatusCode.Conflict);
        await rig.SetupAsync();
        rig.Platform.Writers.Add("VS Code");
        Assert.Equal(["VS Code"], (await rig.Agent.GetAsync("api/upgrade/writers"))["open"]!.AsArray().Select(n => n!.GetValue<string>()));
        await rig.Agent.PostAsync("api/upgrade/convert", null, HttpStatusCode.Conflict);
        rig.Platform.Writers.Clear();

        var view = await rig.ConvertAsync();
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.All(view["steps"]!.AsArray(), s => Assert.Equal("done", s!["state"]!.GetValue<string>()));
        Assert.DoesNotContain(view["items"]!.AsArray(), i => i!["state"]!.GetValue<string>() is "failed" or "pending");
        await rig.AssertArrivedAsync();
        Assert.Contains(view["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("leases end", StringComparison.Ordinal));
        Assert.Contains(view["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("legacy-upstream.json", StringComparison.Ordinal));
        Assert.Equal(["agent-vm:construct backup recipients add --label construct-account", "agent-vm:construct backup recipients remove " + rig.Fingerprint],
            rig.Platform.VmCalls.Select(c => c.Instance + ":" + c.Command));

        // A second run changes nothing: no record written, no VM touched, no job started.
        var snapshot = await rig.SnapshotAsync();
        var again = await rig.ConvertAsync();
        Assert.Equal("converted", again["stage"]!.GetValue<string>());
        Assert.All(again["items"]!.AsArray(), i => Assert.Equal("skipped", i!["state"]!.GetValue<string>()));
        Assert.Equal(snapshot, await rig.SnapshotAsync());

        // The old files are what they were, and nothing secret reached the agent's own files.
        Assert.Equal(before, rig.Fixture.Digests());
        rig.AssertNoSecretInAgentFiles();
    }

    [Fact]
    public async Task Several_instances_with_a_remote_host_arrive_and_the_host_keeps_its_pin_and_sign_in()
    {
        using var remote = new EngineHost();
        var remoteToken = await remote.AddUserAsync("alice");
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            SingleVm(f);
            f.SshKey("construct_work_ed25519");
            f.SshKey("construct_far_ed25519");
            f.Instances(new JsonObject
            {
                ["version"] = 1, ["defaultInstance"] = "agent-vm",
                ["instances"] = new JsonObject
                {
                    ["agent-vm"] = new JsonObject(),
                    ["work"] = new JsonObject { ["backend"] = "hyperv-local" },
                    ["far"] = new JsonObject
                    {
                        ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                        ["service"] = new JsonObject { ["url"] = remote.Url, ["auth"] = "token" },
                    },
                },
            });
            f.Write(Path.Combine("The-Construct", "instances", "work.json"), """{"version":1,"instance":"work","projects":["api"],"t3code":false}""");
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        }, vms: ["agent-vm", "work"]);
        var before = rig.Fixture.Digests();

        await rig.EngineStepAsync();
        // The shared host was added for sign-in with the old pin and token.
        var remoteView = (await rig.Agent.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!;
        Assert.Equal(remote.Pin, remoteView["pin"]!.GetValue<string>());
        Assert.True(remoteView["signedIn"]!.GetValue<bool>());
        await rig.SetupAsync();
        var view = await rig.ConvertAsync();

        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        await rig.AssertArrivedAsync();
        var work = (await rig.Local.Services.GetRequiredService<IVmDesiredStateSource>().GetDesiredAsync("work", CancellationToken.None))!.Desired;
        Assert.Equal(["api"], work.Projects);
        Assert.Equal("false", work.Settings.T3Code);
        var host = view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>().StartsWith("host:", StringComparison.Ordinal))!;
        Assert.Equal("done", host["state"]!.GetValue<string>());
        var state = await rig.Agent.GetAsync("api/state");
        var added = state["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!;
        Assert.True(added["enrolled"]!.GetValue<bool>() || added["signedIn"]!.GetValue<bool>());
        // The account key reached every VM, then the old key came off each.
        Assert.Equal(["agent-vm", "far", "work"], rig.Platform.VmCalls.Where(c => c.Command.Contains("add")).Select(c => c.Instance).Order());
        Assert.Equal(3, rig.Platform.VmCalls.Count(c => c.Command.Contains("remove")));

        var snapshot = await rig.SnapshotAsync();
        await rig.ConvertAsync();
        Assert.Equal(snapshot, await rig.SnapshotAsync());
        Assert.Equal(before, rig.Fixture.Digests());
        rig.AssertNoSecretInAgentFiles(remoteToken);
    }

    [Fact]
    public async Task A_run_cut_in_the_middle_and_repeated_ends_as_an_uncut_run()
    {
        string uncut;
        await using (var rig = await UpgradeRig.CreateAsync(SingleVm))
        {
            await rig.EngineStepAsync();
            await rig.SetupAsync();
            await rig.ConvertAsync();
            uncut = await rig.SummaryAsync();
        }

        foreach (var cutAt in new[] { "branch:vm:web", "secret:auto-npm", "vm:agent-vm" })
        {
            await using var rig = await UpgradeRig.CreateAsync(SingleVm);
            var before = rig.Fixture.Digests();
            await rig.EngineStepAsync();
            await rig.SetupAsync();
            rig.Agent.Host.Upgrade.BeforeItem = id => id == cutAt ? throw new AgentFailure(500, "cut", "The agent stopped here.") : Task.CompletedTask;
            await Assert.ThrowsAsync<AgentFailure>(() => rig.Agent.Host.Upgrade.ConvertAsync(CancellationToken.None));
            Assert.NotEqual("converted", rig.Agent.Host.Upgrade.Bridge.Read().Stage);
            rig.Agent.Host.Upgrade.BeforeItem = null;

            var view = await rig.ConvertAsync();
            Assert.Equal("converted", view["stage"]!.GetValue<string>());
            Assert.True(uncut == await rig.SummaryAsync(), cutAt + "\n" + await rig.SummaryAsync() + "\n" + string.Join("\n", rig.Platform.VmCalls));
            Assert.Equal(before, rig.Fixture.Digests());
        }
    }

    [Fact]
    public async Task An_account_set_up_with_another_vault_key_copies_no_secret_and_says_why()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        // The vault appears only after the setup took a new K.
        var vault = Path.Combine(rig.Fixture.LocalAppData, "The-Construct-Vault");
        Directory.Move(vault, vault + ".aside");
        await rig.SetupAsync();
        Directory.Move(vault + ".aside", vault);

        var view = await rig.ConvertAsync();
        Assert.NotEqual("converted", view["stage"]!.GetValue<string>());
        var failed = view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "vault")!;
        Assert.Equal("failed", failed["state"]!.GetValue<string>());
        Assert.Contains("not the old one", failed["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty((await rig.Agent.GetAsync("api/vault/secrets")).AsArray());
    }

    [Fact]
    public async Task The_cli_converts_in_process_and_reports_each_item()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        var output = new StringWriter();
        // With the agent running the command goes through its routes.
        var exit = await ConvertCommand.RunAsync(rig.Agent.Host.Options, null, null, output, CancellationToken.None);
        Assert.True(exit == 0, output.ToString());
        Assert.Contains("Profile web", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("converted", rig.Agent.Host.Upgrade.Bridge.Read().Stage);
    }
}

/// <summary>One upgrade under test: the old install, this PC's engine (fake Hyper-V and a guest that adopts), the agent.</summary>
internal sealed class UpgradeRig : IAsyncDisposable
{
    private UpgradeRig(LegacyFixture fixture, FakeUpgradePlatform platform, EngineHost local, AdoptableGuest guest, TestAgent agent, string adminToken)
    {
        Fixture = fixture;
        Platform = platform;
        Local = local;
        Guest = guest;
        Agent = agent;
        AdminToken = adminToken;
    }

    public LegacyFixture Fixture { get; }

    public FakeUpgradePlatform Platform { get; }

    public EngineHost Local { get; }

    public AdoptableGuest Guest { get; }

    public TestAgent Agent { get; }

    public string AdminToken { get; }

    public string? Fingerprint => Fixture.BackupFingerprint;

    public static async Task<UpgradeRig> CreateAsync(Action<LegacyFixture> build, string[]? vms = null)
    {
        var fixture = new LegacyFixture();
        build(fixture);
        var guest = new AdoptableGuest();
        var local = new EngineHost(services => services.AddSingleton<ISshRunner>(guest));
        var driver = local.Services.GetRequiredService<FakeHypervisorDriver>();
        foreach (var vm in vms ?? ["agent-vm"])
        {
            driver.Facts[vm] = new HypervisorVmFacts(Guid.NewGuid().ToString("D"), VmState.Running, 4, 8L << 30, 128L << 30, "{" + AdoptableGuest.Firmware + "}");
            driver.SetState(vm, VmState.Running);
        }

        var token = await local.AddUserAsync("alice", Role.Admin);
        var platform = new FakeUpgradePlatform(fixture);
        var agent = await TestAgent.StartAsync(upgrade: platform);
        return new UpgradeRig(fixture, platform, local, guest, agent, token);
    }

    /// <summary>Step 1, with the elevated installer replaced by the token file it writes.</summary>
    public async Task EngineStepAsync()
    {
        Agent.Host.Upgrade.EngineInstaller = _ =>
        {
            var file = Path.Combine(Agent.Directory, "engine-token.json");
            File.WriteAllText(file, new JsonObject { ["url"] = Local.Url, ["user"] = "alice", ["token"] = AdminToken, ["certificateSha256"] = Local.Pin }.ToJsonString());
            return Task.FromResult(file);
        };
        await Agent.PostAsync("api/upgrade/engine", null, HttpStatusCode.Accepted);
        await Agent.Host.Upgrade.Work;
        var view = await Agent.GetAsync("api/upgrade");
        Assert.True(view["error"] is null, view.ToJsonString());
        Assert.Equal("done", view["steps"]![0]!["state"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(Agent.Directory, "engine-token.json")));
    }

    /// <summary>Step 2: the existing first-device setup against the store engine the engine step chose.</summary>
    public async Task SetupAsync()
    {
        var store = (await Agent.GetAsync("api/upgrade"))["storeEngine"]!.GetValue<string>();
        var answer = await Agent.PostAsync("api/device/setup", new { engine = store, deviceName = "pc", vaultMode = "available" });
        Assert.False(string.IsNullOrEmpty(answer["recoveryCode"]?.GetValue<string>()));
    }

    public async Task<JsonNode> ConvertAsync()
    {
        await Agent.PostAsync("api/upgrade/convert", null, HttpStatusCode.Accepted);
        await Agent.Host.Upgrade.Work;
        return await Agent.GetAsync("api/upgrade");
    }

    private string LocalEngine => Agent.Host.Upgrade.Bridge.Read().LocalEngine!;

    public async Task AssertArrivedAsync()
    {
        var engine = LocalEngine;
        // Profiles from main, the VM-only one from the branch, the branch edit as a conflict with the branch as ours.
        var web = await Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/web");
        Assert.Equal("yarn install", web["profile"]!["provisionCommands"]![0]!.GetValue<string>());
        await Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/api");
        await Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/vmonly");
        await Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/default", HttpStatusCode.NotFound);
        var conflict = Assert.Single((await Agent.GetAsync($"api/engines/{engine}/api/v1/profile-conflicts")).AsArray())!;
        Assert.Equal("web", conflict["profile"]!.GetValue<string>());
        Assert.Equal("pnpm install", conflict["ours"]!["provisionCommands"]![0]!.GetValue<string>());
        Assert.Equal("yarn install", conflict["theirs"]!["provisionCommands"]![0]!.GetValue<string>());
        Assert.Equal("npm install", conflict["base"]!["provisionCommands"]![0]!.GetValue<string>());

        // Secrets under the old K, values and auto-approve intact.
        Assert.Equal("ghp_Fixture_Secret_1", (await Agent.PostAsync("api/vault/secrets/github/reveal", null))["value"]!.GetValue<string>());
        Assert.Equal("octo", (await Agent.PostAsync("api/vault/secrets/github/reveal", null))["username"]!.GetValue<string>());
        var secrets = (await Agent.GetAsync("api/vault/secrets")).AsArray();
        Assert.True(secrets.Single(s => s!["name"]!.GetValue<string>() == "auto-npm")!["autoApprove"]!.GetValue<bool>());

        // The VM adopted with the old client's settings; it kept running.
        var desired = (await Local.Services.GetRequiredService<IVmDesiredStateSource>().GetDesiredAsync("agent-vm", CancellationToken.None))!.Desired;
        Assert.Equal(["web", "api"], desired.Projects);
        Assert.Equal("Ada", desired.Settings.GitUserName);
        Assert.Equal("alice", (await Local.Services.GetRequiredService<IVmRepository>().GetAsync("agent-vm", CancellationToken.None))!.Owner);
        Assert.Equal(VmState.Running, Local.Services.GetRequiredService<FakeHypervisorDriver>().StateOf("agent-vm"));

        // The old backup key kept as a setting record; the upstream links as the agent's data; the channel and the microphone.
        var account = (await Agent.GetAsync("api/state"))["device"]!["account"]!.GetValue<string>();
        var records = (await Local.Services.GetRequiredService<RecordStore>().FeedAsync(account, 0, TimeSpan.Zero, 10_000, CancellationToken.None)).Records;
        Assert.Single(records, r => r.Id.StartsWith("setting:legacy-backup-key:", StringComparison.Ordinal) && !r.Deleted);
        var upstream = JsonNode.Parse(File.ReadAllText(Path.Combine(Agent.Directory, "legacy-upstream.json")))!;
        Assert.NotNull(upstream["files"]!["manifest/remotes.json"]);
        var device = JsonNode.Parse(File.ReadAllText(Path.Combine(Agent.Directory, "device.json")))!;
        Assert.Equal("stable", device["channel"]!.GetValue<string>());
        Assert.Contains($"{engine}/agent-vm", device["mic"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    /// <summary>What a run changes: every record and revision, the VM calls, the engine's jobs.</summary>
    public async Task<string> SnapshotAsync()
    {
        var account = (await Agent.GetAsync("api/state"))["device"]!["account"]!.GetValue<string>();
        var records = (await Local.Services.GetRequiredService<RecordStore>().FeedAsync(account, 0, TimeSpan.Zero, 10_000, CancellationToken.None)).Records;
        var jobs = await Local.Services.GetRequiredService<IJobQueryStore>().ListAsync(CancellationToken.None);
        return string.Join('\n', records.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.Id} {r.Revision} {r.Deleted}"))
            + $"\nvm calls {Platform.VmCalls.Count}\njobs {jobs.Count}\nguest {Guest.Commands.Count}";
    }

    /// <summary>The end state without revisions: which records, profiles, conflicts, secrets and VMs exist, for comparing two runs.</summary>
    public async Task<string> SummaryAsync()
    {
        var engine = LocalEngine;
        var account = (await Agent.GetAsync("api/state"))["device"]!["account"]!.GetValue<string>();
        var records = (await Local.Services.GetRequiredService<RecordStore>().FeedAsync(account, 0, TimeSpan.Zero, 10_000, CancellationToken.None)).Records;
        var kinds = records.Where(r => !r.Deleted && r.Kind is not (RecordKinds.Device or RecordKinds.Engine))
            .Select(r => r.Id.StartsWith("setting:legacy-backup-key:", StringComparison.Ordinal) ? "setting:legacy-backup-key" : r.Id).Order(StringComparer.Ordinal);
        var profiles = new List<string>();
        foreach (var name in new[] { "api", "web", "vmonly" })
            profiles.Add(name + "=" + (await Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/{name}"))["profile"]!.ToJsonString());
        var conflicts = (await Agent.GetAsync($"api/engines/{engine}/api/v1/profile-conflicts")).AsArray()
            .Select(c => $"{c!["profile"]}|{c["base"]?.ToJsonString()}|{c["ours"]?.ToJsonString()}|{c["theirs"]?.ToJsonString()}");
        var secrets = (await Agent.GetAsync("api/vault/secrets")).AsArray().Select(s => s!["name"]!.GetValue<string>());
        var vms = (await Local.Services.GetRequiredService<IVmRepository>().ListAsync(null, CancellationToken.None)).Select(v => v.Name + ":" + v.Owner);
        return string.Join('\n', kinds.Concat(profiles).Concat(conflicts).Concat(secrets).Concat(vms))
            + $"\nrecipients {Platform.VmCalls.Count(c => c.Command.Contains("add"))}/{Platform.VmCalls.Count(c => c.Command.Contains("remove"))}";
    }

    /// <summary>No secret of the old install in the agent's own files (state, bridge, settings, upstream data).</summary>
    public void AssertNoSecretInAgentFiles(params string[] more)
    {
        var text = string.Concat(Directory.EnumerateFiles(Agent.Directory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".key", StringComparison.Ordinal)).Select(File.ReadAllText));
        foreach (var secret in new[] { "ghp_Fixture_Secret_1", "npm_Fixture_Secret_2", "b3BlbnNzaC1rZXktdjEAAAAAfixture", AdminToken, Convert.ToBase64String(Fixture.VaultKey) }.Concat(more))
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await Agent.DisposeAsync();
        Local.Dispose();
        Fixture.Dispose();
    }
}
