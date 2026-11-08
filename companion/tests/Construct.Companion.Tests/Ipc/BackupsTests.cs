using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Backups;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion.Tests.Ipc;

// Config backups (docs/plans/host-config-backups.md §3.7, §3.8): the panel's Backups commands, the per-VM override,
// the module in the instance state, and the status poll's one notification per (instance, failingSince).
public sealed class BackupsTests
{
    private static string Id(char c) => new(c, 32);
    private const string Remote = "{\"version\":1,\"defaultInstance\":\"remote-vm\",\"instances\":{\"agent-vm\":null,\"remote-vm\":{\"backend\":\"hyperv-remote\",\"sshHost\":\"guest.example\",\"scriptsDir\":\"/fake/scripts\",\"service\":{\"url\":\"http://localhost:7462\",\"auth\":\"negotiate\"}}}}";
    private sealed class Host(HttpTests.Harness h, FakeRemoteApi api) : IAsyncDisposable
    {
        public HttpTests.Harness H => h; public FakeRemoteApi Api => api;
        public string FailingSince = "2026-11-07T10:00:00Z";
        public bool Feature = true;
        public JsonObject? Stored;
        public MessageDispatcher Dispatcher => h.App.Services.GetRequiredService<MessageDispatcher>();
        public FakePrompts Prompts => h.Get<FakePrompts, IPrompts>();
        public FakeLauncher Launcher => h.Get<FakeLauncher, ILauncher>();
        public FakeToastRaiser Toasts => h.Get<FakeToastRaiser, IToastRaiser>();
        public CompanionInstance Entry(string name) => h.App.Services.GetRequiredService<CompanionInstances>().Get(name);
        public Task Send(string name, object message) => Dispatcher.DispatchAsync(name, JsonSerializer.SerializeToNode(message)!.AsObject(), CancellationToken.None);
        public ValueTask DisposeAsync() => h.DisposeAsync();
    }
    private static async Task<Host> Start(bool remote = false, bool backupScript = true)
    {
        var api = new FakeRemoteApi();
        var h = await HttpTests.Harness.Start(s => s.AddSingleton<IRemoteApi>(api), runtimeJobs: false);
        if (backupScript) h.Files.WriteFileAtomic("/fake/scripts/Provision-AgentVM.ps1", "param($InstanceName,$ConfigBranch,[string]$BackupFile,[string]$BackupId,[string]$BackupKeyFile)"u8);
        h.Files.WriteFileAtomic("/fake/scripts/lib/AgentVm.InstanceTarget.ps1", "# name targeting"u8);
        if (remote) h.Files.WriteFileAtomic("/fake/local/The-Construct/instances.json", System.Text.Encoding.UTF8.GetBytes(Remote));
        var host = new Host(h, api);
        api.Handler = request => new(request.Method == "POST" ? 202 : 200, JsonSerializer.SerializeToElement<object>(request.Url.AbsolutePath switch
        {
            "/api/v1/health" => new { status = "ok", apiFeatures = host.Feature ? new[] { "host-admin", "config-backups" } : ["host-admin"] },
            "/api/v1/whoami" => new { name = "alice", role = "user", known = true, enabled = true },
            "/api/v1/vms/remote-vm/backups/status" => new { enabled = true, lastAttemptAt = "2026-11-08T10:00:00Z", lastSuccessAt = "2026-11-06T09:30:00Z",
                lastFailure = new { at = "2026-11-08T10:00:00Z", trigger = "scheduled", reason = "no-recipients", detail = "" }, failingSince = host.FailingSince.Length > 0 ? host.FailingSince : null,
                requestPending = false, count = 2, totalBytes = 3113851289L, nextDueAt = (string?)null },
            "/api/v1/vms/remote-vm/backups" => new { backups = new object[] {
                new { id = Id('a'), createdAt = "2026-11-08T09:00:00Z", trigger = "scheduled", sizeBytes = 1610612736L, sha256 = new string('f', 64), pinnedUntil = (string?)null },
                new { id = Id('b'), createdAt = "2026-11-05T22:15:00Z", trigger = "pre-reinstall", sizeBytes = 1503238553L, sha256 = new string('e', 64), pinnedUntil = "2026-11-19T22:15:00Z" } } },
            "/api/v1/vms/remote-vm/backups/settings" when request.Method == "PUT" => Settings(host.Stored = JsonNode.Parse(request.Body!.Value.GetRawText())!.AsObject()),
            "/api/v1/vms/remote-vm/backups/settings" => Settings(host.Stored),
            "/api/v1/vms/remote-vm/backups/request" => new { requestPending = true },
            _ => new { }
        }));
        return host;
        static object Settings(JsonObject? stored) => new { intervalHours = stored?["intervalHours"]?.GetValue<int>(), keep = stored?["keep"]?.GetValue<int>(),
            effective = new { enabled = true, intervalHours = 24, keep = 4 }, host = new { enabled = true, intervalHours = 24, keep = 4, maxKeep = 10 } };
    }
    // The command a non-elevated launch runs: `& '<script>' <args>`, base64 in the last argument.
    private static string Command(ProcessInvocation invocation) => System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(invocation.Arguments[^1]));
    private static List<string> Refusals(IpcEvents events, Action act)
    {
        using var subscription = events.Subscribe(); act(); var found = new List<string>();
        while (subscription.Reader.TryRead(out var e)) if (e.Event == "message" && e.Data.GetProperty("message").TryGetProperty("error", out var error)) found.Add(error.GetString()!);
        return found;
    }

    [Fact]
    public async Task LocalVmSavesAFreshEncryptedBackupToThisPc()
    {
        await using var h = await Start(); h.Prompts.SaveFiles.Enqueue("C:/Users/someone/agent-vm.cbk");
        await h.Send("agent-vm", new { type = "command", id = "backupSave" });
        var prompt = Assert.IsType<SaveFilePrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Matches("^agent-vm-[0-9]{8}T[0-9]{6}Z\\.cbk$", prompt.DefaultPath); Assert.Contains("*.cbk", prompt.Filter);
        var command = Command(Assert.Single(h.Launcher.Detached)); Assert.Empty(h.Launcher.Elevated);
        Assert.Equal("& '/fake/scripts/Provision-AgentVM.ps1' -FromPanel -Action 'backup-save' -BackupFile 'C:/Users/someone/agent-vm.cbk'", command);
        // Cancelling the dialog launches nothing.
        h.Prompts.SaveFiles.Enqueue(null); await h.Send("agent-vm", new { type = "command", id = "backupSave" });
        Assert.Single(h.Launcher.Detached);
    }
    [Fact]
    public async Task OlderScriptsAreRefusedInsteadOfLaunched()
    {
        await using var h = await Start(backupScript: false);
        var refusals = Refusals(h.H.App.Services.GetRequiredService<IpcEvents>(), () => h.Send("agent-vm", new { type = "command", id = "backupEnroll" }).GetAwaiter().GetResult());
        Assert.Contains(refusals, r => r.Contains("needs a newer Construct", StringComparison.Ordinal));
        Assert.Empty(h.Launcher.Detached); Assert.Empty(h.Launcher.Elevated);
    }
    [Fact]
    public async Task KeyAndFileActionsUseFileDialogsAndConfirmRestores()
    {
        await using var h = await Start();
        h.Prompts.SaveFiles.Enqueue("C:/Users/someone/construct-backup-key.json"); await h.Send("agent-vm", new { type = "command", id = "backupKeyExport" });
        h.Prompts.OpenFiles.Enqueue("C:/Users/someone/other-pc-key.json"); await h.Send("agent-vm", new { type = "command", id = "backupKeyImport" });
        h.Prompts.OpenFiles.Enqueue("C:/Users/someone/dev.cbk"); h.Prompts.Confirmations.Enqueue(false); await h.Send("agent-vm", new { type = "command", id = "backupRestoreFile" });
        h.Prompts.OpenFiles.Enqueue("C:/Users/someone/dev.cbk"); h.Prompts.Confirmations.Enqueue(true); await h.Send("agent-vm", new { type = "command", id = "backupRestoreFile" });
        h.Prompts.OpenFiles.Enqueue(null); await h.Send("agent-vm", new { type = "command", id = "backupKeyImport" });
        await h.Send("agent-vm", new { type = "command", id = "backupEnroll" });
        Assert.Equal([
            "& '/fake/scripts/Provision-AgentVM.ps1' -FromPanel -Action 'backup-key-export' -BackupKeyFile 'C:/Users/someone/construct-backup-key.json'",
            "& '/fake/scripts/Provision-AgentVM.ps1' -FromPanel -Action 'backup-key-import' -BackupKeyFile 'C:/Users/someone/other-pc-key.json'",
            "& '/fake/scripts/Provision-AgentVM.ps1' -FromPanel -Action 'backup-restore' -BackupFile 'C:/Users/someone/dev.cbk'",
            "& '/fake/scripts/Provision-AgentVM.ps1' -FromPanel -Action 'backup-enroll'"], h.Launcher.Detached.Select(Command));
        Assert.Equal(BackupProtocol.KeyFileName, h.Prompts.Shown.OfType<SaveFilePrompt>().Single().DefaultPath);
        Assert.Contains("dev.cbk", h.Prompts.Shown.OfType<ConfirmationPrompt>().First().Title);
        Assert.Empty(h.Launcher.Elevated);
    }
    [Fact]
    public async Task RemoteRestoreAndSaveUseTheHostListAndTheInstanceName()
    {
        await using var h = await Start(remote: true);
        h.Prompts.Confirmations.Enqueue(true); await h.Send("remote-vm", new { type = "command", id = "backupRestore", backup = Id('b') });
        var confirm = Assert.IsType<ConfirmationPrompt>(Assert.Single(h.Prompts.Shown));
        Assert.Contains("2026-11-05 22:15 UTC", confirm.Title); Assert.Contains("remote-vm", confirm.Title);
        Assert.Equal($"& '/fake/scripts/Provision-AgentVM.ps1' -FromPanel -InstanceName 'remote-vm' -Action 'backup-restore' -BackupId '{Id('b')}'", Command(Assert.Single(h.Launcher.Detached)));
        // An id the host no longer lists is refused before any prompt or launch.
        var refusals = Refusals(h.H.App.Services.GetRequiredService<IpcEvents>(), () => h.Send("remote-vm", new { type = "command", id = "backupRestore", backup = Id('c') }).GetAwaiter().GetResult());
        Assert.Contains(refusals, r => r.Contains("no longer listed", StringComparison.Ordinal)); Assert.Single(h.Launcher.Detached); Assert.Single(h.Prompts.Shown);
        // Save to this PC: the selected host backup is downloaded, under the default name.
        h.Prompts.SaveFiles.Enqueue("C:/Users/someone/remote.cbk"); await h.Send("remote-vm", new { type = "command", id = "backupSave", backup = Id('a') });
        Assert.Matches("^remote-vm-[0-9]{8}T[0-9]{6}Z\\.cbk$", h.Prompts.Shown.OfType<SaveFilePrompt>().Single().DefaultPath);
        Assert.Equal($"& '/fake/scripts/Provision-AgentVM.ps1' -FromPanel -InstanceName 'remote-vm' -Action 'backup-save' -BackupFile 'C:/Users/someone/remote.cbk' -BackupId '{Id('a')}'", Command(h.Launcher.Detached[^1]));
        // Malformed ids never reach the queue.
        Assert.Equal(400, Assert.Throws<IpcFailure>(() => h.Dispatcher.Validate("remote-vm", new JsonObject { ["type"] = "command", ["id"] = "backupRestore", ["backup"] = "../x" })).Status);
        Assert.Equal(400, Assert.Throws<IpcFailure>(() => h.Dispatcher.Validate("remote-vm", new JsonObject { ["type"] = "command", ["id"] = "backupSave", ["backup"] = Id('A') })).Status);
        h.Dispatcher.Validate("remote-vm", new JsonObject { ["type"] = "command", ["id"] = "backupSave", ["backup"] = "" });
        Assert.Empty(h.Launcher.Elevated);
    }
    [Fact]
    public async Task BackUpNowAsksTheHostAndLocalVmsAreRefused()
    {
        await using var h = await Start(remote: true);
        await h.Send("remote-vm", new { type = "command", id = "backupNow" });
        var request = Assert.Single(h.Api.Requests, r => r.Url.AbsolutePath == "/api/v1/vms/remote-vm/backups/request");
        Assert.Equal("POST", request.Method); Assert.Equal("{}", request.Body!.Value.GetRawText());
        var state = h.H.App.Services.GetRequiredService<StateAggregation>().State("remote-vm")["state"]!["backups"]!;
        Assert.True(state["remote"]!.GetValue<bool>()); Assert.Equal(2, state["items"]!.AsArray().Count);
        Assert.Empty(h.Launcher.Detached);
    }
    [Fact]
    public async Task OverrideIsValidatedAgainstTheHostMaximumBeforeTheWrite()
    {
        await using var h = await Start(remote: true);
        var refusals = Refusals(h.H.App.Services.GetRequiredService<IpcEvents>(), () => h.Send("remote-vm", new { type = "saveBackupSettings", settings = new { intervalHours = "721", keep = "11" } }).GetAwaiter().GetResult());
        Assert.Contains(refusals, r => r.Contains("0 to 720", StringComparison.Ordinal) && r.Contains("1 to 10", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Api.Requests, r => r.Method == "PUT");
        await h.Send("remote-vm", new { type = "saveBackupSettings", settings = new { intervalHours = "", keep = "6" } });
        var put = Assert.Single(h.Api.Requests, r => r.Method == "PUT");
        Assert.Equal("/api/v1/vms/remote-vm/backups/settings", put.Url.AbsolutePath);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["intervalHours"] = null, ["keep"] = 6 }, JsonNode.Parse(put.Body!.Value.GetRawText())));
        // The refreshed module shows the stored override.
        Assert.Equal("6", h.H.App.Services.GetRequiredService<StateAggregation>().State("remote-vm")["state"]!["backups"]!["override"]!["keep"]!.GetValue<string>());
        Assert.Equal(400, Assert.Throws<IpcFailure>(() => h.Dispatcher.Validate("remote-vm", new JsonObject { ["type"] = "saveBackupSettings" })).Status);
    }
    [Fact]
    public async Task TheModuleFollowsTheInstanceAndTheHostFeature()
    {
        await using var h = await Start(remote: true);
        h.H.Files.WriteFileAtomic("/fake/local/The-Construct/backup/pc-key.json", System.Text.Encoding.UTF8.GetBytes("\uFEFF{\"fingerprint\":\"" + new string('c', 64) + "\",\"label\":\"Work PC\",\"exportedAt\":null}"));
        var hosts = h.H.App.Services.GetRequiredService<HostAdministration>(); var state = h.H.App.Services.GetRequiredService<StateAggregation>();
        await hosts.RefreshExtrasAsync(h.Entry("remote-vm"), CancellationToken.None);
        var model = state.State("remote-vm")["state"]!["backups"]!;
        Assert.True(model["remote"]!.GetValue<bool>()); Assert.Contains("failing since", model["warning"]!.GetValue<string>()); Assert.Equal("no-recipients", model["warningReason"]!.GetValue<string>());
        Assert.True(model["items"]![1]!["pinned"]!.GetValue<bool>()); Assert.Contains("not exported", model["keyWarning"]!.GetValue<string>());
        // The snapshot an attached extension window reads carries it as well.
        Assert.Equal(JsonValueKind.Object, state.Snapshot("remote-vm").Backups!.Value.GetProperty("backups").ValueKind);
        // A host without the feature hides the module and is never asked for backups.
        await using var old = await Start(remote: true); old.Feature = false;
        var oldHosts = old.H.App.Services.GetRequiredService<HostAdministration>();
        await oldHosts.RefreshExtrasAsync(old.Entry("remote-vm"), CancellationToken.None);
        Assert.Null(old.H.App.Services.GetRequiredService<StateAggregation>().State("remote-vm")["state"]!["backups"]);
        Assert.DoesNotContain(old.Api.Requests, r => r.Url.AbsolutePath.Contains("/backups", StringComparison.Ordinal));
        // A local VM gets the file and key buttons only.
        await using var local = await Start();
        await local.H.App.Services.GetRequiredService<HostAdministration>().RefreshExtrasAsync(local.Entry("agent-vm"), CancellationToken.None);
        var localModel = local.H.App.Services.GetRequiredService<StateAggregation>().State("agent-vm")["state"]!["backups"]!;
        Assert.False(localModel["remote"]!.GetValue<bool>()); Assert.Empty(localModel["items"]!.AsArray());
    }
    [Fact]
    public async Task StatusPollRaisesOneNotificationPerFailureStreakAcrossRestarts()
    {
        await using var h = await Start(remote: true);
        var services = h.H.App.Services; var warnings = services.GetRequiredService<BackupWarnings>();
        await warnings.CheckAllAsync(CancellationToken.None);
        var toast = Assert.Single(h.Toasts.Toasts);
        Assert.Contains("Config backups failing", toast.Xml); Assert.Contains("remote-vm", toast.Xml); Assert.Contains("construct://open?instance=remote-vm", toast.Xml);
        await warnings.CheckAllAsync(CancellationToken.None);
        Assert.Single(h.Toasts.Toasts);
        Assert.Equal(h.FailingSince, StateJson.Text(h.Entry("remote-vm").Store.ReadState()[BackupProtocol.StreakKey]));
        // A restart reads the remembered streak from the instance's state: no second notification.
        var restarted = new BackupWarnings(services.GetRequiredService<CompanionInstances>(), services.GetRequiredService<HostAdministration>(),
            h.Toasts, services.GetRequiredService<IClock>(), services.GetRequiredService<IpcLogs>());
        await restarted.CheckAllAsync(CancellationToken.None);
        Assert.Single(h.Toasts.Toasts);
        // A success ends the streak; the next failure is a new streak and notifies again.
        h.FailingSince = ""; await restarted.CheckAllAsync(CancellationToken.None);
        Assert.Single(h.Toasts.Toasts); Assert.False(h.Entry("remote-vm").Store.ReadState().ContainsKey(BackupProtocol.StreakKey));
        h.FailingSince = "2026-11-09T10:00:00Z"; await restarted.CheckAllAsync(CancellationToken.None);
        Assert.Equal(2, h.Toasts.Toasts.Count);
        // Muted notifications are still remembered, so unmuting does not replay an old streak.
        h.Toasts.Availability = ToastAvailability.Muted; h.FailingSince = "2026-11-10T10:00:00Z";
        await restarted.CheckAllAsync(CancellationToken.None);
        Assert.Equal(2, h.Toasts.Toasts.Count); Assert.Equal(h.FailingSince, StateJson.Text(h.Entry("remote-vm").Store.ReadState()[BackupProtocol.StreakKey]));
    }
    [Fact]
    public async Task StatusPollSkipsLocalVmsAndHostsWithoutTheFeature()
    {
        await using var local = await Start();
        await local.H.App.Services.GetRequiredService<BackupWarnings>().CheckAllAsync(CancellationToken.None);
        Assert.Empty(local.Api.Requests); Assert.Empty(local.Toasts.Toasts);
        await using var old = await Start(remote: true); old.Feature = false;
        await old.H.App.Services.GetRequiredService<BackupWarnings>().CheckAllAsync(CancellationToken.None);
        Assert.DoesNotContain(old.Api.Requests, r => r.Url.AbsolutePath.Contains("/backups", StringComparison.Ordinal)); Assert.Empty(old.Toasts.Toasts);
    }
    [Fact]
    public async Task StatusPollRunsShortlyAfterStartAndEveryFifteenMinutes()
    {
        var clock = new FakeClock(); var api = new FakeRemoteApi();
        await using var h = await HttpTests.Harness.Start(s => { s.AddSingleton<IClock>(clock); s.AddSingleton<IRemoteApi>(api); }, runtimeJobs: false);
        h.Files.WriteFileAtomic("/fake/local/The-Construct/instances.json", System.Text.Encoding.UTF8.GetBytes(Remote));
        var statusReads = 0;
        api.Handler = request => new(200, JsonSerializer.SerializeToElement<object>(request.Url.AbsolutePath switch
        {
            "/api/v1/health" => new { status = "ok", apiFeatures = new[] { "config-backups" } },
            "/api/v1/vms/remote-vm/backups/status" => new { enabled = true, failingSince = Interlocked.Increment(ref statusReads) > 0 ? (string?)null : null },
            _ => new { }
        }));
        using var stop = new CancellationTokenSource();
        var poll = h.App.Services.GetRequiredService<BackupWarnings>().PollAsync(stop.Token);
        async Task Step(TimeSpan by, int expected)
        {
            for (var i = 0; i < 200 && clock.PendingDelays == 0; i++) await Task.Delay(10);
            clock.Advance(by);
            for (var i = 0; i < 200 && Volatile.Read(ref statusReads) < expected; i++) await Task.Delay(10);
            Assert.Equal(expected, Volatile.Read(ref statusReads));
        }
        await Step(BackupWarnings.FirstCheck - TimeSpan.FromSeconds(1), 0);
        await Step(TimeSpan.FromSeconds(1), 1);
        await Step(BackupWarnings.Interval, 2);
        stop.Cancel(); await poll;
        Assert.Equal(TimeSpan.FromMinutes(15), BackupWarnings.Interval);
    }
}
