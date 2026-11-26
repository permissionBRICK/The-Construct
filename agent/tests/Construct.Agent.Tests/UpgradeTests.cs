using System.Net;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Construct.Agent.Upgrade;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Store.Records;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Agent.Tests.UpgradeTests;

namespace Construct.Agent.Tests;

/// <summary>
/// The one-time upgrade (plan 11.6, 13.1, 13.2) against fixtures in the old client's formats and the real engine in
/// fake mode: everything arrives, a second run changes nothing, a run cut in the middle and repeated ends as an uncut
/// one, and the old files are byte for byte what they were.
/// </summary>
public sealed class UpgradeTests
{
    internal static readonly Dictionary<string, string> MainProfiles = new()
    {
        ["projects/web.json"] = """{"name":"web","repos":[{"url":"https://example.test/web.git"}],"provisionCommands":["npm install"]}""",
        ["projects/api.json"] = """{"name":"api","repos":[{"url":"https://example.test/api.git"}]}""",
        ["projects/default.json"] = """{"name":"default"}""",
        ["manifest/remotes.json"] = """{"remotes":[{"url":"https://git.example.test/company/configs.git"}]}""",
        ["manifest/api.json"] = """{"remoteUrl":"https://git.example.test/company/configs.git","ref":"main","pathInRemote":"projects/api.json","importedAs":"api","baseCommit":"0123456789abcdef0123456789abcdef01234567"}""",
        ["bases/api.json"] = """{"name":"api","repos":[{"url":"https://example.test/api.git"}]}""",
    };

    /// <summary>The default VM's install: one local VM, no registry, a VM branch with an unmerged edit, a vault, a backup key.</summary>
    internal static void SingleVm(LegacyFixture f)
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

    /// <summary>A row of the dialog: its state and message.</summary>
    internal static (string State, string? Message) Row(JsonNode view, string id)
    {
        var row = view["items"]!.AsArray().SingleOrDefault(i => i!["id"]!.GetValue<string>() == id) ?? throw new Xunit.Sdk.XunitException($"No row {id}: {view["items"]!.ToJsonString()}");
        return (row["state"]!.GetValue<string>(), row["message"]?.GetValue<string>());
    }

    internal static byte[] PemSpki(string pem)
    {
        using var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// A host of the old stack (no <c>/engine</c>, no <c>/device/backup-key</c>) whose own update API offers the new version;
    /// staging it with auto-apply turns it into the new stack (the real engine behind).
    /// </summary>
    internal sealed class OldUntilStaged : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public const string NewCommit = "b62124e768e638e339c21fa7c93fb88e5d522843";

        public bool Staged => Stages > 0;

        public int Stages { get; private set; }

        /// <summary>How many stage requests it refuses first (409).</summary>
        public int Refusals { get; set; }

        /// <summary>False: the user is not its admin, and its update API refuses them (403).</summary>
        public bool Admin { get; init; } = true;

        /// <summary>Its admin updated it meanwhile: it answers as the new stack.</summary>
        public bool UpdatedElsewhere { get; set; }

        /// <summary>Requests to the store it answered 404 to: the old stack has none.</summary>
        public int StoreCalls { get; private set; }

        /// <summary>Holds a stage request until set.</summary>
        public TaskCompletionSource? HoldStage { get; set; }

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(call => context =>
            {
                if (Staged || UpdatedElsewhere) return call(context);
                var path = context.Request.Path;
                if (path.StartsWithSegments("/api/v1/engine") || path.StartsWithSegments("/api/v1/device/backup-key"))
                {
                    context.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                if (path.StartsWithSegments("/api/v1/store"))
                {
                    StoreCalls++;
                    context.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                if (!Admin && path.StartsWithSegments("/api/v1/host/updates"))
                {
                    context.Response.StatusCode = 403;
                    return Task.CompletedTask;
                }

                if (path == "/api/v1/host/updates/status")
                    return context.Response.WriteAsync("""{"supportsAutoApply":true,"current":null}""");
                if (path == "/api/v1/host/updates/check")
                    return context.Response.WriteAsync(new JsonObject
                    {
                        ["installed"] = new JsonObject { ["commit"] = new string('a', 40) },
                        ["latest"] = new JsonObject { ["commit"] = NewCommit, ["releaseTag"] = "host-" + NewCommit, ["compatible"] = true },
                    }.ToJsonString());
                if (path == "/api/v1/host/updates/stage")
                {
                    if (Refusals > 0)
                    {
                        Refusals--;
                        context.Response.StatusCode = 409;
                        return context.Response.WriteAsync("""{"code":"update-busy"}""");
                    }

                    return HoldStage is { } hold ? hold.Task.ContinueWith(_ => Stage(context), TaskScheduler.Default).Unwrap() : Stage(context);
                }

                return call(context);
            });
            next(app);
        };

        private Task Stage(HttpContext context)
        {
            Stages++;
            // The update installs the offered release: the host answers healthy with its commit.
            ((FakeReleaseInfo)context.RequestServices.GetRequiredService<IReleaseInfo>()).Installed = new InstalledRelease(NewCommit, "1.0.0-test", null, "test");
            context.Response.StatusCode = 202;
            return context.Response.WriteAsync("""{"updateId":"u1","state":"staged"}""");
        }
    }

    /// <summary>An engine without the new stack's identity route, as a host of the old stack answers.</summary>
    internal sealed class NoEngineRoute : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(call => context =>
            {
                // The old stack has no /engine and no /device/backup-key (checked against a real one, 2026-11-21).
                if (context.Request.Path.StartsWithSegments("/api/v1/engine") || context.Request.Path.StartsWithSegments("/api/v1/device/backup-key"))
                {
                    context.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                return call(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task The_engine_step_says_when_Windows_must_restart_and_goes_on_after_it()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        // The shared engine setup, with the installer reporting that Hyper-V needs a restart (EngineSetupTests covers its result file).
        rig.Agent.Host.Upgrade.EngineInstaller = _ => throw new AgentFailure(409, "restart-needed", "Windows must restart to turn on Hyper-V.");
        await rig.Agent.PostAsync("api/upgrade/engine", null, HttpStatusCode.Accepted);
        await rig.Agent.Host.Upgrade.Work;
        var view = await rig.Agent.GetAsync("api/upgrade");
        Assert.True(view["error"] is null, view.ToJsonString());
        Assert.Equal("restart", view["steps"]![0]!["state"]!.GetValue<string>());
        Assert.Equal("Windows must restart to turn on Hyper-V.", view["steps"]![0]!["message"]!.GetValue<string>());
        Assert.Null(rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine);

        await rig.Agent.PostAsync("api/device/restart", null, HttpStatusCode.Accepted);
        Assert.Equal(1, rig.Platform.Restarts);

        // After the restart the step is there again and runs through.
        await rig.EngineStepAsync();
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
        // The upstream links work in the new stack: no note about them (R8).
        Assert.DoesNotContain(view["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("upstream", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1 tracked", view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "upstream")!["message"]!.GetValue<string>());
        // The account key goes on once the engine runs the VM (after its switch-over), through the engine: nothing over the
        // old client's SSH. The old key comes off through the engine too, which checked the account key is there.
        Assert.Empty(rig.Platform.VmCalls);
        Assert.Equal(("done", "Enrolled"), Row(view, "recipient:agent-vm"));
        var accountFingerprint = System.Security.Cryptography.SHA256.HashData(PemSpki(await rig.Agent.Host.Agent.BackupPublicKeyAsync(CancellationToken.None)));
        Assert.Equal([Convert.ToHexStringLower(accountFingerprint)], rig.Guest.Recipients);

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
    public async Task Move_over_ends_by_reprovisioning_the_adopted_VM_and_a_failed_reprovision_is_retried_alone()
    {
        // One click (owner, 2026-11-21): the move leaves the VM on the new guest payload, its last row.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        rig.Guest.FailProvisions = 1;

        var first = await rig.ConvertAsync();
        var row = first["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "reprovision:agent-vm")!;
        Assert.True(row["state"]!.GetValue<string>() == "failed", row.ToJsonString());
        Assert.NotEqual("converted", first["stage"]!.GetValue<string>());
        // Only that row failed; the adoption before it is done.
        Assert.Equal(["reprovision:agent-vm"], first["items"]!.AsArray().Where(i => i!["state"]!.GetValue<string>() == "failed").Select(i => i!["id"]!.GetValue<string>()));
        Assert.Equal(1, rig.Guest.Provisions);

        var second = await rig.ConvertAsync();
        row = second["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "reprovision:agent-vm")!;
        Assert.True(row["state"]!.GetValue<string>() == "done", row.ToJsonString());
        Assert.Equal("converted", second["stage"]!.GetValue<string>());
        Assert.Equal(2, rig.Guest.Provisions);
        var jobs = await rig.Local.Services.GetRequiredService<IJobQueryStore>().ListAsync(CancellationToken.None);
        Assert.Equal(2, jobs.Count(j => j.Kind == "provision" && j.VmName == "agent-vm"));

        // A third run starts no job.
        await rig.ConvertAsync();
        Assert.Equal(2, rig.Guest.Provisions);
    }

    [Fact]
    public async Task Move_over_shows_the_reprovision_in_its_row_and_one_bar_for_the_whole_move()
    {
        // R7: the row carries the job's own percentage and step; the dialog has one bar for everything.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Guest.HoldProvision = hold;

        await rig.Agent.PostAsync("api/upgrade/convert", null, HttpStatusCode.Accepted);
        JsonNode? row = null, progress = null, view = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (row?["percent"] is null)
        {
            if (DateTime.UtcNow > until)
            {
                var jobs = await rig.Local.Services.GetRequiredService<IJobQueryStore>().ListAsync(CancellationToken.None);
                Assert.Fail("the reprovision row never showed the job's progress: " + view?.ToJsonString() + "\n" + string.Join("\n", jobs.Select(j => j.Kind + " " + j.State + " " + string.Join(" | ", j.Progress.Select(p => p.Text + "/" + p.Overall + "/" + p.Label)))));
            }

            await Task.Delay(50);
            view = await rig.Agent.GetAsync("api/upgrade");
            row = view["items"]!.AsArray().SingleOrDefault(i => i!["id"]!.GetValue<string>() == "reprovision:agent-vm");
            progress = view["progress"];
        }

        Assert.Equal("running", row!["state"]!.GetValue<string>());
        // The action and the VM, short enough to leave room for the job's step beside it.
        Assert.Equal("Reprovision agent-vm", row["label"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(row["message"]?.GetValue<string>()));
        Assert.InRange(row["percent"]!.GetValue<int>(), 0, 100);
        Assert.NotNull(progress);
        Assert.InRange(progress!["percent"]!.GetValue<int>(), 20, 99);
        Assert.Equal("Reprovision agent-vm", progress["label"]!.GetValue<string>());

        hold.SetResult();
        await rig.Agent.Host.Upgrade.Work;
        var done = await rig.Agent.GetAsync("api/upgrade");
        Assert.Equal("converted", done["stage"]!.GetValue<string>());
        Assert.True(done["progress"] is null, "the bar goes with the running step");
        row = done["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "reprovision:agent-vm")!;
        Assert.Equal(("done", "On the new version"), (row["state"]!.GetValue<string>(), row["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_local_VM_that_is_off_is_started_adopted_and_reprovisioned_and_its_account_key_row_ends_enrolled()
    {
        // Field run of Move over: the VM was off. The account key row ran first over SSH ("Not reached (exit 255); retried
        // later") and the adoption refused the VM; nothing started it.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        var driver = rig.Local.Services.GetRequiredService<FakeHypervisorDriver>();
        driver.SetState("agent-vm", VmState.Off);
        rig.Platform.VmExit = _ => driver.StateOf("agent-vm") == VmState.Running ? 0 : 255;
        await rig.EngineStepAsync();
        await rig.SetupAsync();

        var view = await rig.ConvertAsync();

        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.Equal(("done", "Switched over"), Row(view, "vm:agent-vm"));
        Assert.Equal(("done", "Enrolled"), Row(view, "recipient:agent-vm"));
        Assert.Equal(("done", "On the new version"), Row(view, "reprovision:agent-vm"));
        Assert.Contains("start:agent-vm", driver.Calls);
        Assert.Equal(VmState.Running, driver.StateOf("agent-vm"));
        Assert.Empty(rig.Platform.VmCalls);
    }

    [Fact]
    public async Task A_failed_adoption_says_the_engines_reason()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        var driver = rig.Local.Services.GetRequiredService<FakeHypervisorDriver>();
        driver.SetState("agent-vm", VmState.Off);
        driver.Reachable = false;
        await rig.EngineStepAsync();
        await rig.SetupAsync();

        var view = await rig.ConvertAsync();

        var (state, message) = Row(view, "vm:agent-vm");
        Assert.Equal("failed", state);
        Assert.Contains("did not answer over SSH", message);
    }

}

/// <summary>The old install folder after the move: its scripts and its install images. Split from <see cref="UpgradeTests"/> so the groups run in parallel.</summary>
public sealed class UpgradeOldInstallTests
{
    [Fact]
    public async Task After_the_move_the_old_install_folders_scripts_act_only_through_the_agent()
    {
        // R14: an old T3 Code Desktop or panel must not run the old client's reprovision against a moved VM.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        var folder = rig.Fixture.InstallFolder;
        string[] old = ["Provision-AgentVM.ps1", "Create-AgentVM.ps1", "Update-T3Code.ps1", "Get-ConstructT3PairingLink.ps1", "Update-Construct.ps1", "install.ps1"];
        foreach (var name in old) await File.WriteAllTextAsync(Path.Combine(folder, name), "# the old client's " + name);
        await rig.EngineStepAsync();
        await rig.SetupAsync();

        // Not before the move is complete.
        rig.Guest.FailProvisions = 1;
        Assert.NotEqual("converted", (await rig.ConvertAsync())["stage"]!.GetValue<string>());
        Assert.Equal("# the old client's Provision-AgentVM.ps1", await File.ReadAllTextAsync(Path.Combine(folder, "Provision-AgentVM.ps1")));

        Assert.Equal("converted", (await rig.ConvertAsync())["stage"]!.GetValue<string>());
        var repo = RepoRoot();
        foreach (var name in OldInstallScripts.Shims)
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(repo, name)), await File.ReadAllBytesAsync(Path.Combine(folder, name)));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(repo, "lib", "Construct.AgentShim.ps1")), await File.ReadAllBytesAsync(Path.Combine(folder, "lib", "Construct.AgentShim.ps1")));
        foreach (var name in new[] { "Provision-AgentVM.ps1", "Create-AgentVM.ps1" })
        {
            var text = await File.ReadAllTextAsync(Path.Combine(folder, name));
            Assert.Contains("moved to the new Construct", text, StringComparison.Ordinal);
            Assert.Contains("exit 1", text, StringComparison.Ordinal);
            Assert.DoesNotContain(text, c => c > 127);
        }

        // An install moved before this version, or a script Windows held then: the agent's start puts them in place.
        await File.WriteAllTextAsync(Path.Combine(folder, "Provision-AgentVM.ps1"), "# the old client's again");
        await rig.RestartAsync();
        await FakeDevice.WaitAsync(() => File.ReadAllText(Path.Combine(folder, "Provision-AgentVM.ps1")).Contains("moved to the new Construct", StringComparison.Ordinal),
            "the old script replaced at start");
    }

    [Fact]
    public async Task An_old_script_that_cannot_be_replaced_is_named_in_the_notes_and_replaced_once_it_can_be()
    {
        // Review of R14: a held (or read-only) Provision-AgentVM.ps1 stayed runnable against the moved VM, and nothing said so.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        var provision = Path.Combine(rig.Fixture.InstallFolder, "Provision-AgentVM.ps1");
        await File.WriteAllTextAsync(provision, "# the old client's Provision-AgentVM.ps1");
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        rig.Agent.Host.Upgrade.ScriptsRetry = TimeSpan.FromMilliseconds(50);
        bool Moved() => File.ReadAllText(provision).Contains("moved to the new Construct", StringComparison.Ordinal);
        async Task<string[]> NotesAsync() => (await rig.Agent.GetAsync("api/upgrade"))["notes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

        // Held by another program (Windows refuses the write; .NET's own lock does the same here).
        using (new FileStream(provision, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal("converted", (await rig.ConvertAsync())["stage"]!.GetValue<string>());
            var note = Assert.Single(await NotesAsync(), n => n.StartsWith(OldInstallScripts.LeftNote, StringComparison.Ordinal));
            Assert.Contains(provision, note, StringComparison.Ordinal);
            Assert.Equal([provision], rig.Agent.Host.Upgrade.Bridge.Read().ScriptsLeft);
        }

        // Let go: the retry replaces it, and the note goes.
        await FakeDevice.WaitAsync(Moved, "the held script replaced by the retry");
        await rig.Agent.Host.Upgrade.Scripts.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(rig.Agent.Host.Upgrade.Bridge.Read().ScriptsLeft);
        Assert.DoesNotContain(await NotesAsync(), n => n.StartsWith(OldInstallScripts.LeftNote, StringComparison.Ordinal));

        // Held at the agent's start: named again, and the next start replaces it.
        await File.WriteAllTextAsync(provision, "# the old client's again");
        using (new FileStream(provision, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await rig.RestartAsync();
            await FakeDevice.WaitAsync(() => rig.Agent.Host.Upgrade.Bridge.Read().ScriptsLeft.Count == 1, "the held script named at start");
            Assert.Contains(await NotesAsync(), n => n.StartsWith(OldInstallScripts.LeftNote, StringComparison.Ordinal));
        }

        await rig.RestartAsync();
        await rig.Agent.Host.Upgrade.Scripts.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Moved());
        Assert.DoesNotContain(await NotesAsync(), n => n.StartsWith(OldInstallScripts.LeftNote, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_linked_old_script_or_a_linked_folder_is_not_written_through()
    {
        // Review of R14: the replacement followed links, so a file outside the old install was overwritten.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        var folder = rig.Fixture.InstallFolder;
        var outside = Path.Combine(rig.Fixture.Home, "elsewhere");
        Directory.CreateDirectory(outside);
        var mine = Path.Combine(outside, "mine.ps1");
        var update = Path.Combine(outside, "update.ps1");
        await File.WriteAllTextAsync(mine, "# not Construct's");
        await File.WriteAllTextAsync(update, "# not Construct's either");
        File.CreateSymbolicLink(Path.Combine(folder, "Provision-AgentVM.ps1"), mine);
        File.CreateSymbolicLink(Path.Combine(folder, "Update-T3Code.ps1"), update);
        // A folder on the way linked elsewhere: the shim library would land there.
        if (Directory.Exists(Path.Combine(folder, "lib"))) Directory.Delete(Path.Combine(folder, "lib"), true);
        Directory.CreateSymbolicLink(Path.Combine(folder, "lib"), outside);
        await File.WriteAllTextAsync(Path.Combine(folder, "Create-AgentVM.ps1"), "# the old client's Create-AgentVM.ps1");
        await rig.EngineStepAsync();
        await rig.SetupAsync();

        Assert.Equal("converted", (await rig.ConvertAsync())["stage"]!.GetValue<string>());

        Assert.Equal("# not Construct's", await File.ReadAllTextAsync(mine));
        Assert.Equal("# not Construct's either", await File.ReadAllTextAsync(update));
        Assert.Equal(["mine.ps1", "update.ps1"], Directory.EnumerateFiles(outside).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray());
        // A regular script beside them is replaced as before; the linked ones are named.
        Assert.Contains("moved to the new Construct", await File.ReadAllTextAsync(Path.Combine(folder, "Create-AgentVM.ps1")), StringComparison.Ordinal);
        var left = rig.Agent.Host.Upgrade.Bridge.Read().ScriptsLeft;
        Assert.Equal(new[] { "Provision-AgentVM.ps1", "Update-T3Code.ps1", Path.Combine("lib", "Construct.AgentShim.ps1") }.Select(n => Path.Combine(folder, n)).Order(StringComparer.Ordinal).ToArray(),
            left.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>The checkout this test runs from: the shims ship from its root.</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Update-T3Code.ps1")) && Directory.Exists(Path.Combine(dir.FullName, "agent"))) return dir.FullName;
        throw new InvalidOperationException("No checkout above the test.");
    }

    [Fact]
    public async Task A_complete_conversion_deletes_the_old_clients_install_images_in_its_own_folder_only()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        // The old client's autoinstall image (3 GB): the engine's ISO builder refuses a patched image as its source (F33).
        var image = Path.Combine(rig.Fixture.InstallFolder, "agent-vm-autoinstall.iso");
        var copy = Path.Combine(rig.Fixture.Home, "Downloads", "agent-vm-autoinstall.iso");
        var other = Path.Combine(rig.Fixture.InstallFolder, "notes.iso.txt");
        foreach (var file in new[] { image, copy, other })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "iso");
        }

        await rig.EngineStepAsync();
        await rig.SetupAsync();
        var view = await rig.ConvertAsync();

        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.False(File.Exists(image));
        Assert.True(File.Exists(copy), "a copy outside Construct's own folders stays");
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task An_install_named_outside_the_old_clients_root_keeps_its_install_images()
    {
        // An old install unpacked elsewhere and named with "from" (a copy in Downloads) is the user's: its images stay.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        var outside = Path.Combine(rig.Fixture.Home, "Downloads", "old-construct");
        Directory.CreateDirectory(outside);
        File.Copy(Path.Combine(rig.Fixture.InstallFolder, ".construct-settings.json"), Path.Combine(outside, ".construct-settings.json"));
        var image = Path.Combine(outside, "agent-vm-autoinstall.iso");
        await File.WriteAllTextAsync(image, "iso");

        await rig.EngineStepAsync();
        await rig.SetupAsync();
        await rig.Agent.PostAsync("api/upgrade/convert", new JsonObject { ["from"] = outside }, HttpStatusCode.Accepted);
        await rig.Agent.Host.Upgrade.Work;

        Assert.Equal("converted", (await rig.Agent.GetAsync("api/upgrade"))["stage"]!.GetValue<string>());
        Assert.True(File.Exists(image));
    }

}

/// <summary>Runs of the dialog and the conversion: several instances, a cut run, another vault key, configuration edges. Split from <see cref="UpgradeTests"/> so the groups run in parallel.</summary>
public sealed class UpgradeRunTests
{
    [Fact]
    public async Task The_dialog_reads_the_old_config_repository_once_per_run_and_the_conversion_reads_it_afresh()
    {
        // The dialog asks for its view several times per step; on the FatPC (2026-11-21) every view ran some 60 git
        // processes. The old files do not change while the agent holds the Companion's mutex.
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        static string[] Profiles(JsonNode view) => view["found"]!["profiles"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(["api", "web"], Profiles(await rig.Agent.GetAsync("api/upgrade")));

        rig.Fixture.Write(Path.Combine("The-Construct", "config", "projects", "late.json"), """{"name":"late","repos":[]}""");
        Assert.Equal(["api", "web"], Profiles(await rig.Agent.GetAsync("api/upgrade")));

        // A new agent run reads the old repository afresh while the upgrade is still open.
        await rig.RestartAsync();
        Assert.Equal(["api", "late", "web"], Profiles(await rig.Agent.GetAsync("api/upgrade")));

        rig.Fixture.Write(Path.Combine("The-Construct", "config", "projects", "later.json"), """{"name":"later","repos":[]}""");
        Assert.Equal(["api", "late", "web"], Profiles(await rig.Agent.GetAsync("api/upgrade")));

        await rig.EngineStepAsync();
        await rig.SetupAsync();
        Assert.Equal("converted", (await rig.ConvertAsync())["stage"]!.GetValue<string>());
        var engine = rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine!;
        await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/later");

        // Once converted, the agent no longer re-scans the old repository on startup.
        await rig.RestartAsync();
        var after = await rig.Agent.GetAsync("api/upgrade");
        Assert.Equal("converted", after["stage"]!.GetValue<string>());
        Assert.Null(after["found"]);
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
                // No "agent-vm" entry: the old registry synthesizes the default VM, and so does the converter.
                ["version"] = 1, ["defaultInstance"] = "work",
                ["instances"] = new JsonObject
                {
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

        await rig.Agent.GetAsync("api/upgrade");
        await rig.Agent.Host.Upgrade.Probe;
        Assert.Empty((await rig.Agent.GetAsync("api/upgrade"))["found"]!["stayOld"]!.AsArray());
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
        Assert.Empty(view["oldHosts"]!.AsArray());
        var host = view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>().StartsWith("host:", StringComparison.Ordinal))!;
        Assert.Equal("done", host["state"]!.GetValue<string>());
        var state = await rig.Agent.GetAsync("api/state");
        var added = state["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!;
        Assert.True(added["enrolled"]!.GetValue<bool>() || added["signedIn"]!.GetValue<bool>());
        // The account key reached every VM, then the old key came off each: on the adopted VMs through this PC's engine
        // (after their switch-over), on the one the shared host does not run under that name over the old client's SSH.
        Assert.Equal(["far"], rig.Platform.VmCalls.Where(c => c.Command.Contains("add")).Select(c => c.Instance).Order());
        Assert.Equal(["far"], rig.Platform.VmCalls.Where(c => c.Command.Contains("remove")).Select(c => c.Instance));
        Assert.DoesNotContain(rig.Fingerprint!, rig.Guest.Recipients);
        Assert.Contains(view["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == $"unrecipient:work:{rig.Fingerprint}" && i["state"]!.GetValue<string>() == "done");

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
        Assert.Equal("Other vault key; secrets not copied", failed["message"]!.GetValue<string>());
        Assert.Empty((await rig.Agent.GetAsync("api/vault/secrets")).AsArray());
    }

    [Fact]
    public async Task Edges_arrive_two_branches_merge_a_custom_branch_a_folder_edit_and_a_failed_switch_over_is_retried()
    {
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            SingleVm(f);
            f.SshKey("construct_work_ed25519");
            f.Instances(new JsonObject
            {
                ["version"] = 1,
                ["instances"] = new JsonObject { ["agent-vm"] = new JsonObject(), ["work"] = new JsonObject { ["configBranch"] = "WORK" } },
            });
            // A second VM changed another field of web; the custom branch WORK added a profile.
            f.Branch("vm-other", new Dictionary<string, string> { ["projects/web.json"] =
                """{"name":"web","repos":[{"url":"https://example.test/web.git"}],"provisionCommands":["yarn install"],"hostPackages":["jq"]}""" });
            f.Branch("WORK", new Dictionary<string, string> { ["projects/fromwork.json"] = """{"name":"fromwork","repos":[]}""" });
            // Edited in the folder before the old client's next sync tick.
            f.Write(Path.Combine("The-Construct", "config", "projects", "api.json"), """{"name":"api","repos":[{"url":"https://example.test/api-new.git"}]}""");
            f.Vault([("github", "ghp_Fixture_Secret_1", "octo"), ("auto-npm", "npm_Fixture_Secret_2", null), ("spaced", " pass word ", null)]);
        }, vms: ["agent-vm", "work"]);
        var before = rig.Fixture.Digests();
        rig.Guest.FailSwitches = 1;
        await rig.EngineStepAsync();
        await rig.SetupAsync();

        var first = await rig.ConvertAsync();
        Assert.NotEqual("converted", first["stage"]!.GetValue<string>());
        Assert.Single(first["items"]!.AsArray(), i => i!["state"]!.GetValue<string>() == "failed");
        // The retry resumes the VM the failed adoption left registered.
        var second = await rig.ConvertAsync();
        Assert.Equal("converted", second["stage"]!.GetValue<string>());

        var engine = rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine!;
        var web = (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/web"))["profile"]!;
        Assert.Equal("jq", web["hostPackages"]![0]!.GetValue<string>());
        await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/fromwork");
        Assert.Equal("https://example.test/api-new.git", (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/api"))["profile"]!["repos"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(" pass word ", (await rig.Agent.PostAsync("api/vault/secrets/spaced/reveal", null))["value"]!.GetValue<string>());
        Assert.NotNull(await rig.Local.Services.GetRequiredService<IVmRepository>().GetAsync("work", CancellationToken.None));
        Assert.Equal(before, rig.Fixture.Digests());
    }

}

/// <summary>The old PC key's backups, the recovery step and the notes. Split from <see cref="UpgradeTests"/> so the groups run in parallel.</summary>
public sealed class UpgradeKeyAndNoteTests
{
    [Fact]
    public async Task A_backup_made_for_the_old_PC_key_restores_with_the_key_the_converter_kept()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        Assert.Equal("converted", (await rig.ConvertAsync())["stage"]!.GetValue<string>());
        var engine = rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine!;

        // A backup from before the move: its key wrapped only for the old PC key (and a recipient nobody holds).
        const string identity = "AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ";
        using var old = System.Security.Cryptography.RSA.Create();
        old.ImportFromPem(rig.Fixture.BackupPublicPem);
        var id = Guid.NewGuid().ToString("n");
        var keys = new JsonObject
        {
            ["format"] = "construct-backup-keys/1", ["backupId"] = id, ["createdAt"] = "2026-10-01T10:00:00Z",
            ["recipients"] = new JsonArray(
                new JsonObject { ["fingerprint"] = new string('0', 64), ["label"] = "other", ["wrapped"] = Convert.ToBase64String(new byte[384]) },
                new JsonObject
                {
                    ["fingerprint"] = rig.Fingerprint, ["label"] = "Construct",
                    ["wrapped"] = Convert.ToBase64String(old.Encrypt(System.Text.Encoding.ASCII.GetBytes(identity), System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256)),
                }),
        }.ToJsonString();
        var now = DateTimeOffset.UtcNow;
        await rig.Local.Services.GetRequiredService<IBackupStore>().InsertAsync(
            new ConfigBackup(id, "alice", "agent-vm", "manual", BackupStates.Complete, now, now, 10, new string('a', 64), null, keys), CancellationToken.None);

        var started = await rig.Agent.PostAsync($"api/engines/{engine}/vms/agent-vm/backups/{id}/restore", new JsonObject { ["requestId"] = "old" });
        Assert.DoesNotContain(identity, started.ToJsonString());
        var job = await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/jobs/{started["jobId"]!.GetValue<string>()}");
        Assert.Equal("restore", job["kind"]!.GetValue<string>());
        Assert.DoesNotContain(identity, job.ToJsonString());
        Assert.DoesNotContain(identity, rig.Agent.StateFileText());
    }

    [Fact]
    public async Task The_recovery_step_is_done_only_once_the_code_was_confirmed_and_a_reload_gets_a_new_code()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        var (first, firstIssue) = await rig.IssueCodeAsync();
        string Recovery(JsonNode view) => view["steps"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == "recovery")!["state"]!.GetValue<string>();

        // Issued, not confirmed: the step is not done, the conversion refused, and so after a restart of the agent.
        Assert.Equal("todo", Recovery(await rig.Agent.GetAsync("api/upgrade")));
        Assert.True((await rig.Agent.GetAsync("api/state"))["device"]!["recoveryUnconfirmed"]!.GetValue<bool>());
        await rig.Agent.PostAsync("api/upgrade/convert", null, HttpStatusCode.Conflict);
        await Assert.ThrowsAsync<AgentFailure>(() => rig.Agent.Host.Upgrade.ConvertAsync(CancellationToken.None));
        await rig.RestartAsync();
        Assert.Equal("todo", Recovery(await rig.Agent.GetAsync("api/upgrade")));

        // The page after a reload asks for a new code: the earlier one stops working at the store.
        var again = await rig.Agent.PostAsync("api/device/recovery-code/again", null);
        var second = again["recoveryCode"]!.GetValue<string>();
        Assert.NotEqual(first, second);
        // Another window still showing the first code cannot confirm it: the current one is the second.
        await rig.Agent.PostAsync("api/device/recovery-code/kept", new { issue = firstIssue }, HttpStatusCode.Conflict);
        Assert.Equal("todo", Recovery(await rig.Agent.GetAsync("api/upgrade")));
        var account = (await rig.Agent.GetAsync("api/state"))["device"]!["account"]!.GetValue<string>();
        await using (var other = await TestAgent.StartAsync())
        {
            var engine = await other.AddEngineAsync(rig.Local);
            await other.PostAsync("api/device/recover", new { engine, account, recoveryCode = first, deviceName = "other" }, HttpStatusCode.Forbidden);
        }

        await rig.Agent.PostAsync("api/device/recovery-code/kept", new { issue = again["issue"]!.GetValue<long>() }, HttpStatusCode.NoContent);
        Assert.Equal("done", Recovery(await rig.Agent.GetAsync("api/upgrade")));
        // Confirmed: no page can issue another code this way (a stale reload after the confirm).
        await rig.Agent.PostAsync("api/device/recovery-code/again", null, HttpStatusCode.Conflict);
        Assert.False((await rig.Agent.GetAsync("api/state"))["device"]!["recoveryUnconfirmed"]!.GetValue<bool>());
        Assert.Equal("converted", (await rig.ConvertAsync())["stage"]!.GetValue<string>());
        // Neither code reached the agent's files.
        rig.AssertNoSecretInAgentFiles(first, second, first.Replace("-", "", StringComparison.Ordinal), second.Replace("-", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_notes_stay_after_the_conversion_until_read_and_only_for_their_account()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        var view = await rig.ConvertAsync();
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.NotEmpty(view["notes"]!.AsArray());

        // Kept over a restart of the agent.
        await rig.RestartAsync();
        var notes = (await rig.Agent.GetAsync("api/upgrade"))["notes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(view["notes"]!.AsArray().Select(n => n!.GetValue<string>()), notes);

        // Notes written for another account are not shown.
        var bridge = rig.Agent.Host.Upgrade.Bridge;
        var owner = bridge.Read().NotesAccount;
        bridge.Update(b => b with { NotesAccount = "someone-else" });
        Assert.Empty((await rig.Agent.GetAsync("api/upgrade"))["notes"]!.AsArray());
        bridge.Update(b => b with { NotesAccount = owner });

        var read = await rig.Agent.PostAsync("api/upgrade/notes/read", null);
        Assert.Empty(read["notes"]!.AsArray());
        await rig.RestartAsync();
        Assert.Empty((await rig.Agent.GetAsync("api/upgrade"))["notes"]!.AsArray());
    }

    [Fact]
    public async Task A_note_of_an_item_done_before_a_cut_stays_after_the_resumed_run()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        rig.Agent.Host.Upgrade.BeforeItem = id => id == "vm:agent-vm" ? throw new AgentFailure(500, "cut", "The agent stopped here.") : Task.CompletedTask;
        await Assert.ThrowsAsync<AgentFailure>(() => rig.Agent.Host.Upgrade.ConvertAsync(CancellationToken.None));
        rig.Agent.Host.Upgrade.BeforeItem = null;
        await rig.RestartAsync();
        var view = await rig.ConvertAsync();
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.Contains(view["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("leases end", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_notes_cannot_be_read_away_before_the_conversion()
    {
        await using var rig = await UpgradeRig.CreateAsync(SingleVm);
        await rig.Agent.PostAsync("api/upgrade/notes/read", null, HttpStatusCode.Conflict);
    }

}

/// <summary>Hosts on the old stack: shared ones, and the one that keeps the store when there is no local VM. Split from <see cref="UpgradeTests"/> so the groups run in parallel.</summary>
public sealed class UpgradeOldHostTests
{
    [Fact]
    public async Task A_shared_host_on_the_old_stack_is_named_before_the_conversion()
    {
        // The old stack has no GET /engine: this device stays signed in the old way there.
        using var remote = new EngineHost(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, NoEngineRoute>());
        var remoteToken = await remote.AddUserAsync("alice");
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            SingleVm(f);
            f.SshKey("construct_far_ed25519");
            f.Instances(new JsonObject
            {
                ["version"] = 1, ["defaultInstance"] = "agent-vm",
                ["instances"] = new JsonObject
                {
                    ["far"] = new JsonObject
                    {
                        ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                        ["service"] = new JsonObject { ["url"] = remote.Url, ["auth"] = "token" },
                    },
                },
            });
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        });

        // Before the engine step the dialog asks the host with the old pin and token: its VM stays on the old Construct (R1).
        await rig.Agent.GetAsync("api/upgrade");
        await rig.Agent.Host.Upgrade.Probe;
        Assert.Equal(["far"], (await rig.Agent.GetAsync("api/upgrade"))["found"]!["stayOld"]!.AsArray().Select(n => n!.GetValue<string>()));

        await rig.EngineStepAsync();
        Assert.Equal(["127.0.0.1"], (await rig.Agent.GetAsync("api/upgrade"))["oldHosts"]!.AsArray().Select(n => n!.GetValue<string>()));
        await rig.SetupAsync();
        var view = await rig.ConvertAsync();
        Assert.Equal(["127.0.0.1"], view["oldHosts"]!.AsArray().Select(n => n!.GetValue<string>()));
        // A few words (R6): the host and its VM's backup keys are left alone because the host runs the old Construct, not
        // because it disowns the VM.
        var host = view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>().StartsWith("host:", StringComparison.Ordinal))!;
        Assert.Equal("Old Construct, unchanged", host["message"]!.GetValue<string>());
        var far = view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "recipient:far")!;
        Assert.Equal("skipped", far["state"]!.GetValue<string>());
        Assert.Equal("Old Construct, unchanged", far["message"]!.GetValue<string>());
        var added = (await rig.Agent.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!;
        Assert.False(added["enrolled"]!.GetValue<bool>());
        Assert.True(added["signedIn"]!.GetValue<bool>());
        rig.AssertNoSecretInAgentFiles(remoteToken);
    }

    [Fact]
    public async Task A_shared_host_on_the_old_stack_that_its_user_administers_moves_with_the_PC_when_it_offers_the_new_version()
    {
        // R5 (owner, 2026-11-21): Move over brings every Construct over; the old host through its own update API.
        var old = new OldUntilStaged();
        using var remote = new EngineHost(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(old));
        var remoteToken = await remote.AddUserAsync("alice", Role.Admin);
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            SingleVm(f);
            f.SshKey("construct_far_ed25519");
            f.Instances(new JsonObject
            {
                ["version"] = 1, ["defaultInstance"] = "agent-vm",
                ["instances"] = new JsonObject
                {
                    ["far"] = new JsonObject
                    {
                        ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                        ["service"] = new JsonObject { ["url"] = remote.Url, ["auth"] = "token" },
                    },
                },
            });
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        });
        rig.Agent.Host.Upgrade.ShipsAgent = (_, tag, _) => Task.FromResult(tag == "host-" + OldUntilStaged.NewCommit);
        rig.Agent.Host.Upgrade.OldHostPoll = TimeSpan.FromMilliseconds(50);

        // It moves, so its VM does not stay on the old Construct (R1).
        await rig.Agent.GetAsync("api/upgrade");
        await rig.Agent.Host.Upgrade.Probe;
        Assert.Empty((await rig.Agent.GetAsync("api/upgrade"))["found"]!["stayOld"]!.AsArray());
        Assert.False(old.Staged, "asking stages nothing");

        await rig.EngineStepAsync();
        await rig.SetupAsync();
        var view = await rig.ConvertAsync();
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.Null(view["found"]);
        Assert.True(old.Staged);
        string Message(string prefix) => view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>().StartsWith(prefix, StringComparison.Ordinal))!["message"]!.GetValue<string>();
        Assert.Equal("Old Construct; moves next", Message("host:"));
        // Enrolled where the host can enrol this device; a second engine cannot while the store is this PC's (E12): signed in.
        Assert.Contains(Message("host-update:"), new[] { "Moved, enrolled", "Moved, signed in" });
        // None of the host's VMs is the user's: no row to show, and the host is done.
        Assert.DoesNotContain(view["items"]!.AsArray(), i => i!["id"]!.GetValue<string>().StartsWith("host-reprovision:", StringComparison.Ordinal));
        Assert.Contains("host-reprovision:" + LegacyState.Slug(remote.Url), rig.Agent.Host.Upgrade.Bridge.Read().Done);
        Assert.Empty(view["oldHosts"]!.AsArray());
        var added = (await rig.Agent.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!;
        Assert.True(added["enrolled"]!.GetValue<bool>() || added["signedIn"]!.GetValue<bool>());
        // It answers as the new stack now: the agent learned its identity when it connected there.
        Assert.False(string.IsNullOrEmpty(added["engineId"]?.GetValue<string>()), added.ToJsonString());

        // A second run moves nothing again and starts no reprovision there.
        var again = await rig.ConvertAsync();
        Assert.DoesNotContain(again["items"]!.AsArray(), i => i!["id"]!.GetValue<string>().StartsWith("host-update:", StringComparison.Ordinal));
        Assert.DoesNotContain(again["items"]!.AsArray(), i => i!["id"]!.GetValue<string>().StartsWith("host-reprovision:", StringComparison.Ordinal));
        Assert.Equal(1, old.Stages);
    }

    [Fact]
    public async Task A_shared_host_whose_update_does_not_go_through_is_named_in_the_notes_until_it_moves()
    {
        // Review of R5: the row was pending, the move ended as "Moved over", and the converted overview shows no rows.
        var old = new OldUntilStaged { Refusals = 1 };
        using var remote = new EngineHost(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(old));
        var remoteToken = await remote.AddUserAsync("alice", Role.Admin);
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            SingleVm(f);
            f.SshKey("construct_far_ed25519");
            f.Instances(new JsonObject
            {
                ["version"] = 1, ["defaultInstance"] = "agent-vm",
                ["instances"] = new JsonObject
                {
                    ["far"] = new JsonObject
                    {
                        ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                        ["service"] = new JsonObject { ["url"] = remote.Url, ["auth"] = "token" },
                    },
                },
            });
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        });
        rig.Agent.Host.Upgrade.ShipsAgent = (_, tag, _) => Task.FromResult(tag == "host-" + OldUntilStaged.NewCommit);
        rig.Agent.Host.Upgrade.OldHostPoll = TimeSpan.FromMilliseconds(50);
        await rig.EngineStepAsync();
        await rig.SetupAsync();

        var view = await rig.ConvertAsync();
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        var row = view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>().StartsWith("host-update:", StringComparison.Ordinal))!;
        Assert.Equal("pending", row["state"]!.GetValue<string>());
        Assert.Contains("Host 127.0.0.1 not moved: Update not started (409). Update tries again.", view["notes"]!.AsArray().Select(n => n!.GetValue<string>()));

        // Move over again: it moves now, and the note goes.
        var again = await rig.ConvertAsync();
        Assert.Equal("done", again["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>().StartsWith("host-update:", StringComparison.Ordinal))!["state"]!.GetValue<string>());
        Assert.DoesNotContain(again["notes"]!.AsArray(), n => n!.GetValue<string>().StartsWith(LegacyConverter.HostNote("127.0.0.1"), StringComparison.Ordinal));
    }

    /// <summary>
    /// The PC's old install has no local VM, only a shared host on the old stack with a stored sign-in (in the field the PC's
    /// own old host service), so the account's store has to go there. The installer of a local engine counts its runs.
    /// </summary>
    private static async Task<(UpgradeRig Rig, EngineHost Remote, Func<int> Installs)> OnlyAnOldHostAsync(OldUntilStaged old, Role role)
    {
        var remote = new EngineHost(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(old));
        var remoteToken = await remote.AddUserAsync("alice", role);
        var rig = await UpgradeRig.CreateAsync(f =>
        {
            SingleVm(f);
            f.SshKey("construct_far_ed25519");
            f.Instances(new JsonObject
            {
                ["version"] = 1, ["defaultInstance"] = "far",
                ["instances"] = new JsonObject
                {
                    ["agent-vm"] = null,
                    ["far"] = new JsonObject
                    {
                        ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                        ["service"] = new JsonObject { ["url"] = remote.Url, ["auth"] = "token" },
                    },
                },
            });
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        });
        rig.Agent.Host.Upgrade.ShipsAgent = (_, tag, _) => Task.FromResult(tag == "host-" + OldUntilStaged.NewCommit);
        rig.Agent.Host.Upgrade.OldHostPoll = TimeSpan.FromMilliseconds(50);
        var installs = 0;
        rig.Agent.Host.Upgrade.EngineInstaller = _ =>
        {
            installs++;
            throw new AgentFailure(500, "test-no-install", "No engine is installed on this PC here.");
        };
        return (rig, remote, () => installs);
    }

    private static async Task<JsonNode> EngineStepAsync(UpgradeRig rig)
    {
        await rig.Agent.PostAsync("api/upgrade/engine", null, HttpStatusCode.Accepted);
        await rig.Agent.Host.Upgrade.Work;
        return await rig.Agent.GetAsync("api/upgrade");
    }

    [Fact]
    public async Task Without_a_local_VM_the_old_host_for_the_store_moves_in_the_first_step_and_the_converter_does_not_move_it_again()
    {
        // Field run: the recovery step set the store up at the old host and stopped with "The store answered 404".
        var old = new OldUntilStaged { HoldStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (rig, remote, installs) = await OnlyAnOldHostAsync(old, Role.Admin);
        using var _ = remote;
        await using var __ = rig;
        var slug = LegacyState.Slug(remote.Url);

        // The step shows the move while the host updates.
        await rig.Agent.PostAsync("api/upgrade/engine", null, HttpStatusCode.Accepted);
        JsonNode? bar = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (bar is null)
        {
            Assert.True(DateTime.UtcNow < until, "the step never showed the host's update");
            await Task.Delay(20);
            bar = (await rig.Agent.GetAsync("api/upgrade"))["progress"];
        }

        Assert.Equal("Updating 127.0.0.1", bar["label"]!.GetValue<string>());
        old.HoldStage!.SetResult();
        await rig.Agent.Host.Upgrade.Work;
        var view = await rig.Agent.GetAsync("api/upgrade");
        Assert.True(view["error"] is null, view.ToJsonString());
        Assert.Equal("skipped", view["steps"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(1, old.Stages);
        // No second engine on this PC: the store is on the moved host.
        Assert.Equal(0, installs());
        Assert.Null(rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine);
        var host = (await rig.Agent.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!;
        Assert.Equal(host["id"]!.GetValue<string>(), view["storeEngine"]!.GetValue<string>());
        Assert.Empty(view["oldHosts"]!.AsArray());

        await rig.SetupAsync();
        var converted = await rig.ConvertAsync();
        Assert.Equal("converted", converted["stage"]!.GetValue<string>());
        Assert.Equal(0, old.StoreCalls);
        // The converter does not move it again; its moved-host step runs: none of its VMs is the user's, so no row, and the
        // host is done.
        Assert.DoesNotContain(converted["items"]!.AsArray(), i => i!["id"]!.GetValue<string>().StartsWith("host-update:", StringComparison.Ordinal));
        Assert.Equal(1, old.Stages);
        Assert.DoesNotContain(converted["items"]!.AsArray(), i => i!["id"]!.GetValue<string>().StartsWith("host-reprovision:", StringComparison.Ordinal));
        Assert.Contains("host-reprovision:" + slug, rig.Agent.Host.Upgrade.Bridge.Read().Done);
    }

    [Fact]
    public async Task Without_a_local_VM_an_old_host_the_user_does_not_administer_stops_the_first_step_until_its_admin_moved_it()
    {
        var old = new OldUntilStaged { Admin = false };
        var (rig, remote, installs) = await OnlyAnOldHostAsync(old, Role.User);
        using var _ = remote;
        await using var __ = rig;

        var view = await EngineStepAsync(rig);
        Assert.Equal("127.0.0.1 still runs the old Construct; its admin has to update it to the new version first. Your old Companion keeps working until then.",
            view["error"]?.GetValue<string>());
        Assert.Equal("todo", view["steps"]![0]!["state"]!.GetValue<string>());
        Assert.True(view["storeEngine"] is null, view.ToJsonString());
        Assert.Equal((0, 0, 0), (old.StoreCalls, old.Stages, installs()));

        // Its admin updated it: Retry goes on, and the store is set up there.
        old.UpdatedElsewhere = true;
        view = await EngineStepAsync(rig);
        Assert.True(view["error"] is null, view.ToJsonString());
        Assert.Empty(view["oldHosts"]!.AsArray());
        await rig.SetupAsync();
        Assert.Equal((0, 0), (old.StoreCalls, old.Stages));
    }

    [Fact]
    public async Task Without_a_local_VM_a_failed_update_of_the_old_host_for_the_store_stops_the_first_step_and_Retry_moves_it()
    {
        var old = new OldUntilStaged { Refusals = 1 };
        var (rig, remote, _) = await OnlyAnOldHostAsync(old, Role.Admin);
        using var __ = remote;
        await using var ___ = rig;

        var view = await EngineStepAsync(rig);
        Assert.Equal("127.0.0.1 did not move to the new version: Update not started (409). Your old Companion keeps working until then; retry.",
            view["error"]?.GetValue<string>());
        Assert.True(view["storeEngine"] is null, view.ToJsonString());

        view = await EngineStepAsync(rig);
        Assert.True(view["error"] is null, view.ToJsonString());
        Assert.Equal(1, old.Stages);
        await rig.SetupAsync();
        Assert.Equal(0, old.StoreCalls);
    }

    [Fact]
    public async Task A_store_the_previous_first_step_left_on_an_old_host_opens_that_step_again_and_Retry_moves_the_host()
    {
        // The field PC: the release before finished the first step with the old host as the store, and the recovery step
        // stopped with "The store answered 404". After the update the first step is open again.
        var old = new OldUntilStaged { Refusals = 1 };
        var (rig, remote, _) = await OnlyAnOldHostAsync(old, Role.Admin);
        using var _ = remote;
        await using var __ = rig;
        await EngineStepAsync(rig); // adds the host and notes it as old, as that step did; its update is refused once here
        var hostId = (await rig.Agent.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!["id"]!.GetValue<string>();
        rig.Agent.Host.Upgrade.Bridge.Update(b => b with { Engine = "skipped", StoreEngine = hostId });

        var view = await rig.Agent.GetAsync("api/upgrade");
        Assert.Equal("todo", view["steps"]![0]!["state"]!.GetValue<string>());
        Assert.True(view["storeEngine"] is null, view.ToJsonString());

        view = await EngineStepAsync(rig);
        Assert.True(view["error"] is null, view.ToJsonString());
        Assert.Equal(hostId, view["storeEngine"]!.GetValue<string>());
        await rig.SetupAsync();
        Assert.Equal((0, 1), (old.StoreCalls, old.Stages));
    }

}

/// <summary>The VMs of old hosts that move with the PC or moved earlier, and the old hosts' plain addresses. Split from <see cref="UpgradeTests"/> so the groups run in parallel.</summary>
public sealed class UpgradeMovedHostTests
{
    /// <summary>
    /// An old host the user administers with one VM of theirs ("far"), which the engine there reaches only as root with the old
    /// client's key (the old provisioning removed the bootstrap key from the seed user), and the PC's old install pointing at it.
    /// </summary>
    private static async Task<(UpgradeRig Rig, EngineHost Remote, OldUntilStaged Old, OldHostGuest Guest)> OldHostWithVmAsync(VmState state = VmState.Running,
        bool movedByItsAdmin = false, string[]? more = null)
    {
        var old = new OldUntilStaged();
        var guest = new OldHostGuest();
        var remote = new EngineHost(services =>
        {
            // Moved by its admin before this device: the new engine from the start, its old guests still locked to the old key.
            if (!movedByItsAdmin) services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(old);
            services.AddSingleton<ISshRunner>(guest);
        });
        remote = movedByItsAdmin ? remote.WithSource() : remote.AddSource(OldUntilStaged.NewCommit);
        var remoteToken = await remote.AddUserAsync("alice", Role.Admin);
        string[] names = ["far", .. more ?? []];
        foreach (var name in names)
        {
            await remote.Services.GetRequiredService<IVmRepository>().AddAsync(new Vm(name, "alice", 2, 4, 50, DateTimeOffset.UtcNow.AddDays(-30),
                state, null, null, IdlePolicy.Disabled, Vm.NoForwards, TokenKind: VmTokenKind.Primary), 10, CancellationToken.None);
            remote.Services.GetRequiredService<FakeHypervisorDriver>().SetState(name, state);
        }

        var rig = await UpgradeRig.CreateAsync(f =>
        {
            SingleVm(f);
            var instances = new JsonObject();
            foreach (var name in names)
            {
                f.SshKey($"construct_{name}_ed25519");
                instances[name] = new JsonObject
                {
                    ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                    ["service"] = new JsonObject { ["url"] = remote.Url, ["auth"] = "token" },
                };
            }

            f.Instances(new JsonObject { ["version"] = 1, ["defaultInstance"] = "agent-vm", ["instances"] = instances });
            // The old client provisioned "far" from this PC with these choices.
            f.Write(Path.Combine("The-Construct", "instances", "far.json"), """{"version":1,"instance":"far","projects":["api"],"t3code":true}""");
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        });
        rig.Agent.Host.Upgrade.ShipsAgent = (_, tag, _) => Task.FromResult(tag == "host-" + OldUntilStaged.NewCommit);
        rig.Agent.Host.Upgrade.OldHostPoll = TimeSpan.FromMilliseconds(50);
        rig.Agent.Host.Upgrade.StartedVmPoll = TimeSpan.FromMilliseconds(50);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        return (rig, remote, old, guest);
    }

    [Fact]
    public async Task The_VMs_on_a_host_its_admin_moved_earlier_get_the_old_clients_key_and_are_reprovisioned_too()
    {
        // A user of someone else's host: its admin moved it, so this device moves no host, but its VMs there still
        // admit only the old client's key and have no settings on the engine.
        var (rig, remote, _, guest) = await OldHostWithVmAsync(movedByItsAdmin: true);
        using var _ = remote;
        await using var __ = rig;
        var slug = LegacyState.Slug(remote.Url);

        var view = await rig.ConvertAsync();

        Assert.Equal(("done", "On the new version"), Row(view, $"host-reprovision:{slug}/far"));
        Assert.Equal(1, guest.Inner.Provisions);
        Assert.True(File.Exists(Constructd.Api.Jobs.ProvisionTargets.AdoptedKeyPath(remote.Services.GetRequiredService<ConstructdOptions>(), "far")));
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        // The account key after the VM's rows: this device is not enrolled at that host (it keeps no store there), so the
        // old client's way enrols it, once the VM's rows are through.
        Assert.Equal(("done", "Enrolled"), Row(view, "recipient:far"));
    }

    [Fact]
    public async Task The_VMs_of_a_moved_old_host_get_the_old_clients_key_first_and_their_reprovision_is_waited_for()
    {
        // Field run: the old host updated itself, then the row asked for the reprovision and said "1 reprovisioning" at
        // once. The engine there dialled the seed user, which the old provisioning had locked out, and nothing ran.
        var (rig, remote, old, guest) = await OldHostWithVmAsync();
        using var _ = remote;
        await using var __ = rig;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.Inner.HoldProvision = hold;
        var slug = LegacyState.Slug(remote.Url);

        await rig.Agent.PostAsync("api/upgrade/convert", null, HttpStatusCode.Accepted);
        // The row waits for the job and shows it.
        JsonNode? row = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (row?["percent"] is null || row["state"]!.GetValue<string>() != "running")
        {
            Assert.True(DateTime.UtcNow < until, "the row never showed the running reprovision: " + row?.ToJsonString());
            await Task.Delay(50);
            row = (await rig.Agent.GetAsync("api/upgrade"))["items"]!.AsArray().SingleOrDefault(i => i!["id"]!.GetValue<string>() == $"host-reprovision:{slug}/far");
        }

        // The action and the VM, not the host: the job's step shows beside it as it is.
        Assert.Equal("Reprovision far", row["label"]!.GetValue<string>());
        Assert.DoesNotContain("far:", row["message"]?.GetValue<string>() ?? "", StringComparison.Ordinal);

        Assert.False(rig.Agent.Host.Upgrade.Work.IsCompleted);
        hold.SetResult();
        await rig.Agent.Host.Upgrade.Work;
        var view = await rig.Agent.GetAsync("api/upgrade");

        Assert.True(old.Staged);
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.Equal(("done", "On the new version"), Row(view, $"host-reprovision:{slug}/far"));
        Assert.Equal(1, guest.Inner.Provisions);
        // The key reached the engine (and the VM as root) before the job: the job dialled root with it, never the seed user.
        var options = remote.Services.GetRequiredService<ConstructdOptions>();
        var kept = Constructd.Api.Jobs.ProvisionTargets.AdoptedKeyPath(options, "far");
        Assert.Equal(LegacyFixture.PrivateKeyText, File.ReadAllText(kept));
        Assert.Contains(guest.Admitted, target => target.IdentityFile == kept);
        Assert.Contains(guest.Inner.Commands, c => c.Contains("bin/provision.sh", StringComparison.Ordinal));
        var jobs = await remote.Services.GetRequiredService<IJobQueryStore>().ListAsync(CancellationToken.None);
        Assert.Equal(JobState.Succeeded, Assert.Single(jobs, j => j.Kind == "provision" && j.VmName == "far").State);
        // The old stack stored no settings for the VM: the old client's are its settings now.
        var desired = (await remote.Services.GetRequiredService<IVmDesiredStateSource>().GetDesiredAsync("far", CancellationToken.None))!.Desired;
        Assert.Equal(["api"], desired.Projects);
        Assert.Equal(("Ada", "true"), (desired.Settings.GitUserName, desired.Settings.T3Code));
        rig.AssertNoSecretInAgentFiles();
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(rig.Agent.Host.Upgrade.Bridge.Path), StringComparison.Ordinal);

        // A second run starts no job there: the host's VMs are all done.
        Assert.Contains("host-reprovision:" + slug, rig.Agent.Host.Upgrade.Bridge.Read().Done);
        var again = await rig.ConvertAsync();
        Assert.DoesNotContain(again["items"]!.AsArray(), i => i!["id"]!.GetValue<string>().StartsWith("host-reprovision:", StringComparison.Ordinal));
        Assert.Equal(1, guest.Inner.Provisions);
    }

    [Fact]
    public async Task A_VM_that_is_off_on_a_moved_host_is_started_gets_the_old_key_and_is_reprovisioned()
    {
        var (rig, remote, _, guest) = await OldHostWithVmAsync(VmState.Off);
        using var _ = remote;
        await using var __ = rig;
        var slug = LegacyState.Slug(remote.Url);

        var view = await rig.ConvertAsync();

        Assert.Equal(("done", "On the new version"), Row(view, $"host-reprovision:{slug}/far"));
        Assert.Equal(VmState.Running, remote.Services.GetRequiredService<FakeHypervisorDriver>().StateOf("far"));
        Assert.Contains("start:far", remote.Services.GetRequiredService<FakeHypervisorDriver>().Calls);
        Assert.Equal(1, guest.Inner.Provisions);
        var options = remote.Services.GetRequiredService<ConstructdOptions>();
        Assert.True(File.Exists(Constructd.Api.Jobs.ProvisionTargets.AdoptedKeyPath(options, "far")));
    }

    [Fact]
    public async Task A_failed_reprovision_on_a_moved_host_fails_its_row_and_Move_over_again_redoes_it()
    {
        var (rig, remote, _, guest) = await OldHostWithVmAsync();
        using var _ = remote;
        await using var __ = rig;
        guest.Inner.FailProvisions = 1;
        var slug = LegacyState.Slug(remote.Url);

        var first = await rig.ConvertAsync();
        var (state, message) = Row(first, $"host-reprovision:{slug}/far");
        Assert.Equal("failed", state);
        Assert.StartsWith("Reprovision failed", message);
        Assert.NotEqual("converted", first["stage"]!.GetValue<string>());
        Assert.Equal([$"host-reprovision:{slug}/far"], first["items"]!.AsArray().Where(i => i!["state"]!.GetValue<string>() == "failed").Select(i => i!["id"]!.GetValue<string>()));

        var second = await rig.ConvertAsync();
        Assert.Equal(("done", "On the new version"), Row(second, $"host-reprovision:{slug}/far"));
        Assert.Equal("converted", second["stage"]!.GetValue<string>());
        Assert.Equal(2, guest.Inner.Provisions);
    }

    [Fact]
    public async Task A_host_VMs_row_saved_as_done_by_the_converter_that_did_not_wait_is_done_again_with_the_key_first()
    {
        // The state the field PC kept: the host moved ("host-update:" done) and "host-vms:<host>" saved as done with
        // "1 reprovisioning", while the engine there had no key for the VM and ran nothing.
        var (rig, remote, _, guest) = await OldHostWithVmAsync();
        using var _ = remote;
        await using var __ = rig;
        var slug = LegacyState.Slug(remote.Url);
        await rig.ConvertAsync();
        var options = remote.Services.GetRequiredService<ConstructdOptions>();
        File.Delete(Constructd.Api.Jobs.ProvisionTargets.AdoptedKeyPath(options, "far"));
        var bridge = rig.Agent.Host.Upgrade.Bridge;
        bridge.Update(b => b with
        {
            Stage = "converting",
            Done = [.. b.Done.Where(d => !d.StartsWith("host-reprovision:", StringComparison.Ordinal)), "host-vms:" + slug],
        });
        Assert.Equal(1, guest.Inner.Provisions);

        var view = await rig.ConvertAsync();

        Assert.Equal(("done", "On the new version"), Row(view, $"host-reprovision:{slug}/far"));
        Assert.Equal(2, guest.Inner.Provisions);
        Assert.True(File.Exists(Constructd.Api.Jobs.ProvisionTargets.AdoptedKeyPath(options, "far")));
        Assert.DoesNotContain(bridge.Read().Done, d => d.StartsWith("host-vms:", StringComparison.Ordinal));
        Assert.Contains("host-reprovision:" + slug, bridge.Read().Done);
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_move_finished_as_converted_with_the_host_VMs_row_that_did_not_wait_opens_again()
    {
        // The other state that converter could leave: everything else moved, so it saved "converted"; the dialog hid the
        // move for good and the host's VMs were never reprovisioned.
        var (rig, remote, _, guest) = await OldHostWithVmAsync();
        using var _ = remote;
        await using var __ = rig;
        var slug = LegacyState.Slug(remote.Url);
        await rig.ConvertAsync();
        var bridge = rig.Agent.Host.Upgrade.Bridge;
        bridge.Update(b => b with
        {
            Stage = "converted",
            Done = [.. b.Done.Where(d => !d.StartsWith("host-reprovision:", StringComparison.Ordinal)), "host-vms:" + slug],
        });

        var open = await rig.Agent.GetAsync("api/upgrade");
        Assert.Equal("converting", open["stage"]!.GetValue<string>());

        var view = await rig.ConvertAsync();
        Assert.Equal(("done", "On the new version"), Row(view, $"host-reprovision:{slug}/far"));
        Assert.Equal(2, guest.Inner.Provisions);
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        Assert.Equal("converted", (await rig.Agent.GetAsync("api/upgrade"))["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_VM_of_a_moved_host_has_its_own_row_and_Move_over_again_redoes_only_the_failed_one()
    {
        var (rig, remote, _, guest) = await OldHostWithVmAsync(more: ["near"]);
        using var _ = remote;
        await using var __ = rig;
        guest.Inner.FailProvisions = 1;
        var slug = LegacyState.Slug(remote.Url);

        // One after the other, by name: "far" fails, "near" still runs and is done.
        var first = await rig.ConvertAsync();
        Assert.Equal("failed", Row(first, $"host-reprovision:{slug}/far").State);
        Assert.Equal(("done", "On the new version"), Row(first, $"host-reprovision:{slug}/near"));
        var rows = first["items"]!.AsArray().Where(i => i!["id"]!.GetValue<string>().StartsWith("host-reprovision:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["Reprovision far", "Reprovision near"], rows.Select(i => i!["label"]!.GetValue<string>()));
        Assert.Equal([$"host-reprovision:{slug}/far"], first["items"]!.AsArray().Where(i => i!["state"]!.GetValue<string>() == "failed").Select(i => i!["id"]!.GetValue<string>()));
        Assert.NotEqual("converted", first["stage"]!.GetValue<string>());
        Assert.DoesNotContain("host-reprovision:" + slug, rig.Agent.Host.Upgrade.Bridge.Read().Done);
        Assert.Equal(2, guest.Inner.Provisions);

        var second = await rig.ConvertAsync();
        Assert.Equal(("done", "On the new version"), Row(second, $"host-reprovision:{slug}/far"));
        Assert.Equal(("skipped", "Done earlier"), Row(second, $"host-reprovision:{slug}/near"));
        Assert.Equal(3, guest.Inner.Provisions);
        Assert.Equal("converted", second["stage"]!.GetValue<string>());
        Assert.Contains("host-reprovision:" + slug, rig.Agent.Host.Upgrade.Bridge.Read().Done);
    }

    [Fact]
    public async Task A_host_saved_as_done_by_the_converter_with_one_row_per_host_is_not_redone()
    {
        // The converter before this one had one row per host ("host-reprovision:<host>"); a PC that saved it as done keeps
        // it: none of that host's VMs is reprovisioned again.
        var (rig, remote, _, guest) = await OldHostWithVmAsync();
        using var _ = remote;
        await using var __ = rig;
        var slug = LegacyState.Slug(remote.Url);
        await rig.ConvertAsync();
        var bridge = rig.Agent.Host.Upgrade.Bridge;
        bridge.Update(b => b with
        {
            Stage = "converting",
            Done = [.. b.Done.Where(d => !d.StartsWith($"host-reprovision:{slug}/", StringComparison.Ordinal))],
        });
        Assert.Contains("host-reprovision:" + slug, bridge.Read().Done);
        Assert.Equal(1, guest.Inner.Provisions);

        var view = await rig.ConvertAsync();

        Assert.DoesNotContain(view["items"]!.AsArray(), i => i!["id"]!.GetValue<string>().StartsWith("host-reprovision:", StringComparison.Ordinal));
        Assert.Equal(1, guest.Inner.Provisions);
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
    }

    /// <summary>
    /// A guest on an old host: its sshd admits root with the old client's key only (the old provisioning removed the
    /// bootstrap key from the seed user, and the engine has no other key); what it admits, <see cref="AdoptableGuest"/> answers.
    /// </summary>
    private sealed class OldHostGuest : ISshRunner
    {
        public AdoptableGuest Inner { get; } = new();

        public List<SshTarget> Admitted { get; } = [];

        private bool Admits(SshTarget target)
        {
            var admitted = target.User == "root" && target.IdentityFile is { } file && File.Exists(file) && File.ReadAllText(file) == LegacyFixture.PrivateKeyText;
            if (admitted) lock (Admitted) Admitted.Add(target);
            return admitted;
        }

        public Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct) =>
            Admits(target) ? Inner.RunAsync(target, command, standardInput, lines, timeout, ct) : Task.FromResult(255);

        public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken ct) =>
            Admits(target) ? Inner.CopyAsync(target, localPath, remotePath, ct) : throw new InvalidOperationException("ssh: closed before authentication");
    }

    [Fact]
    public async Task The_dialog_never_asks_an_old_host_over_plain_http()
    {
        // Review of R1: over http:// the pin is never checked, so the probe sent the old token in the clear.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var received = new System.Text.StringBuilder();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer);
                    lock (received) received.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or IOException)
            {
            }
        });
        try
        {
            await using var rig = await UpgradeRig.CreateAsync(f =>
            {
                SingleVm(f);
                f.SshKey("construct_far_ed25519");
                f.Instances(new JsonObject
                {
                    ["version"] = 1, ["defaultInstance"] = "agent-vm",
                    ["instances"] = new JsonObject
                    {
                        ["far"] = new JsonObject
                        {
                            ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                            ["service"] = new JsonObject { ["url"] = url, ["auth"] = "token" },
                        },
                    },
                });
                f.RemoteHost(url, new string('A', 64), "old-host-token");
            });

            await rig.Agent.GetAsync("api/upgrade");
            await rig.Agent.Host.Upgrade.Probe;
            lock (received) Assert.Equal("", received.ToString());
            Assert.Empty((await rig.Agent.GetAsync("api/upgrade"))["found"]!["stayOld"]!.AsArray());
        }
        finally
        {
            listener.Stop();
            await serve;
        }
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

    [Fact]
    public async Task T3_desktop_artifacts_under_The_Construct_do_not_trigger_the_legacy_upgrade()
    {
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            var artifacts = Path.Combine(f.Root, "artifacts", "t3code");
            Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts, "installed.json"), """{"t3Version":"0.0.1"}""");
        });

        var state = LegacyState.Open(rig.Fixture.LocalAppData, null, rig.Fixture.Home, rig.Platform.LegacyProtection);
        Assert.False(state.Found);

        var view = await rig.Agent.GetAsync("api/upgrade");
        Assert.False(view["legacy"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Fresh_install_marked_fresh_never_offers_legacy_migration_even_when_legacy_files_appear()
    {
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            Directory.CreateDirectory(f.Root);
            File.WriteAllText(Path.Combine(f.Root, "instances.json"), """{"version":1,"instances":{"agent-vm":{}}}""");
        });
        rig.Agent.Host.Upgrade.Bridge.Update(b => b with { Stage = BridgeState.Fresh });

        var view = await rig.Agent.GetAsync("api/upgrade");
        Assert.False(view["legacy"]!.GetValue<bool>());
        Assert.Equal(BridgeState.Fresh, view["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task Already_registered_fresh_install_skips_legacy_migration_and_stamps_bridge_as_fresh()
    {
        await using var rig = await UpgradeRig.CreateAsync(_ => { });
        // Register the device directly (normal first-run setup without touching bridge.json).
        var added = await rig.Agent.PostAsync("api/engines", new { address = rig.Local.Url, pin = rig.Local.Pin, userToken = rig.AdminToken });
        var engineId = added["id"]!.GetValue<string>();
        var setup = await rig.Agent.PostAsync("api/device/setup", new { engine = engineId, deviceName = "pc", vaultMode = "available" });
        await rig.Agent.PostAsync("api/device/recovery-code/kept", new { issue = setup["issue"]!.GetValue<long>() }, HttpStatusCode.NoContent);

        // Even if legacy-looking files exist or show up later, a registered fresh install must not be hijacked into migration.
        Directory.CreateDirectory(rig.Fixture.Root);
        File.WriteAllText(Path.Combine(rig.Fixture.Root, "instances.json"), """{"version":1,"instances":{"agent-vm":{}}}""");

        var view = await rig.Agent.GetAsync("api/upgrade");
        Assert.False(view["legacy"]!.GetValue<bool>());
        Assert.Equal(BridgeState.Fresh, view["stage"]!.GetValue<string>());
        Assert.Equal(BridgeState.Fresh, rig.Agent.Host.Upgrade.Bridge.Read().Stage);

        await rig.RestartAsync();
        var afterRestart = await rig.Agent.GetAsync("api/upgrade");
        Assert.False(afterRestart["legacy"]!.GetValue<bool>());
        Assert.Equal(BridgeState.Fresh, afterRestart["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_remote_only_install_with_materialized_default_agent_vm_and_no_ssh_key_skips_local_engine_and_converts()
    {
        // Save-ConstructInstances wrote the synthesized local `agent-vm` into instances.json alongside remote VMs even
        // on PCs that never provisioned a local VM. Without ~/.ssh/agent_vm_ed25519, the upgrade must skip the local
        // engine (and Hyper-V) and convert directly against the remote host.
        using var remote = new EngineHost().WithSource();
        var remoteToken = await remote.AddUserAsync("alice");
        var installs = 0;
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            f.Settings(new JsonObject
            {
                ["constructRepo"] = "permissionBRICK/The-Construct", ["constructRef"] = "main", ["installedCommit"] = new string('a', 40),
            });
            f.ConfigRepo(MainProfiles);
            f.Vault([("github", "ghp_Fixture_Secret_1", "octo")]);
            f.SshKey("construct_far_ed25519");
            f.Instances(new JsonObject
            {
                ["version"] = 1, ["defaultInstance"] = "far",
                ["instances"] = new JsonObject
                {
                    ["agent-vm"] = new JsonObject
                    {
                        ["backend"] = "hyperv-local", ["vmName"] = "Agent-VM", ["sshHost"] = "agent-vm.mshome.net",
                        ["sshPort"] = 22, ["hostAlias"] = "agent-vm", ["keyName"] = "agent_vm_ed25519", ["configBranch"] = "vm",
                    },
                    ["far"] = new JsonObject
                    {
                        ["backend"] = "hyperv-remote", ["sshHost"] = "127.0.0.1", ["sshPort"] = 2201,
                        ["service"] = new JsonObject { ["url"] = remote.Url, ["auth"] = "token" },
                    },
                },
            });
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        });
        rig.Agent.Host.Upgrade.EngineInstaller = _ =>
        {
            installs++;
            throw new AgentFailure(500, "unexpected-local-install", "Local engine should not be installed for a remote-only PC.");
        };

        var before = await rig.Agent.GetAsync("api/upgrade");
        Assert.True(before["legacy"]!.GetValue<bool>());
        Assert.Empty(before["found"]!["localVms"]!.AsArray());
        Assert.Equal(["far"], before["found"]!["remoteVms"]!.AsArray().Select(n => n!.GetValue<string>()));

        await rig.Agent.PostAsync("api/upgrade/engine", null, HttpStatusCode.Accepted);
        await rig.Agent.Host.Upgrade.Work;
        var afterEngine = await rig.Agent.GetAsync("api/upgrade");
        Assert.True(afterEngine["error"] is null, afterEngine.ToJsonString());
        Assert.Equal("skipped", afterEngine["steps"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(0, installs);
        Assert.Null(rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine);
        var host = (await rig.Agent.GetAsync("api/state"))["engines"]!.AsArray().Single(e => e!["url"]!.GetValue<string>() == remote.Url)!;
        Assert.Equal(host["id"]!.GetValue<string>(), afterEngine["storeEngine"]!.GetValue<string>());

        await rig.SetupAsync();
        var converted = await rig.ConvertAsync();
        Assert.Equal("converted", converted["stage"]!.GetValue<string>());
        Assert.DoesNotContain(converted["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == "vm:agent-vm");
        Assert.DoesNotContain(converted["items"]!.AsArray(), i => i!["state"]!.GetValue<string>() == "failed");
    }

    [Fact]
    public async Task A_companion_only_install_without_instances_json_or_default_ssh_key_skips_local_engine()
    {
        using var remote = new EngineHost().WithSource();
        var remoteToken = await remote.AddUserAsync("alice");
        var installs = 0;
        await using var rig = await UpgradeRig.CreateAsync(f =>
        {
            f.Vault([("github", "ghp_Fixture_Secret_1", "octo")]);
            f.Write(Path.Combine("The-Construct", "companion", "hosts.json"),
                new JsonObject { ["hosts"] = new JsonArray(new JsonObject { ["url"] = remote.Url }) }.ToJsonString());
            f.RemoteHost(remote.Url, remote.Pin, remoteToken);
        });
        rig.Agent.Host.Upgrade.EngineInstaller = _ =>
        {
            installs++;
            throw new AgentFailure(500, "unexpected-local-install", "Local engine should not be installed when no default SSH key exists.");
        };

        var before = await rig.Agent.GetAsync("api/upgrade");
        Assert.True(before["legacy"]!.GetValue<bool>());
        Assert.Empty(before["found"]!["localVms"]!.AsArray());

        await rig.Agent.PostAsync("api/upgrade/engine", null, HttpStatusCode.Accepted);
        await rig.Agent.Host.Upgrade.Work;
        var afterEngine = await rig.Agent.GetAsync("api/upgrade");
        Assert.True(afterEngine["error"] is null, afterEngine.ToJsonString());
        Assert.Equal("skipped", afterEngine["steps"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(0, installs);
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

    public TestAgent Agent { get; private set; }

    public string AdminToken { get; }

    /// <summary>The agent stops and starts again on its directory.</summary>
    public async Task RestartAsync() => Agent = await Agent.RestartAsync(Platform);

    public string? Fingerprint => Fixture.BackupFingerprint;

    public static async Task<UpgradeRig> CreateAsync(Action<LegacyFixture> build, string[]? vms = null)
    {
        var fixture = new LegacyFixture();
        build(fixture);
        var guest = new AdoptableGuest();
        if (fixture.BackupFingerprint is { } old) guest.Recipients.Add(old); // the old client enrolled its PC key
        var local = new EngineHost(services => services.AddSingleton<ISshRunner>(guest)).WithSource();
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

    /// <summary>Step 2: the existing first-device setup against the store engine the engine step chose, and the code confirmed as kept.</summary>
    public async Task SetupAsync()
    {
        var (_, issue) = await IssueCodeAsync();
        await Agent.PostAsync("api/device/recovery-code/kept", new { issue }, HttpStatusCode.NoContent);
    }

    /// <summary>The setup without the confirm; the code it showed and its issue.</summary>
    public async Task<(string Code, long Issue)> IssueCodeAsync()
    {
        var store = (await Agent.GetAsync("api/upgrade"))["storeEngine"]!.GetValue<string>();
        var answer = await Agent.PostAsync("api/device/setup", new { engine = store, deviceName = "pc", vaultMode = "available" });
        var code = answer["recoveryCode"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(code));
        return (code!, answer["issue"]!.GetValue<long>());
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

        // The old backup key kept as a setting record; the upstream link as its record with the stored base at the engine
        // (9.7); the channel and the microphone.
        var account = (await Agent.GetAsync("api/state"))["device"]!["account"]!.GetValue<string>();
        var records = (await Local.Services.GetRequiredService<RecordStore>().FeedAsync(account, 0, TimeSpan.Zero, 10_000, CancellationToken.None)).Records;
        Assert.Single(records, r => r.Id.StartsWith("setting:legacy-backup-key:", StringComparison.Ordinal) && !r.Deleted);
        Assert.Single(records, r => r.Id == Upstream.UpstreamLinks.RecordId("https://git.example.test/company/configs.git") && r.Kind == RecordKinds.Upstream && !r.Deleted);
        Assert.Single(records, r => r.Id == "upstream-applied:api" && r.Kind == RecordKinds.UpstreamApplied && !r.Deleted);
        Assert.False(File.Exists(Path.Combine(Agent.Directory, "legacy-upstream.json")));
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
