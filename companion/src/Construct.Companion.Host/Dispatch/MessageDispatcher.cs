using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Lifecycle;
using RemoteHost = Construct.Companion.Core.Remote.RemoteHost;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Host.Dispatch;

// The C# port of the extension's handleMessage: one inbound webview message in, the same
// outbound messages the webview would have received on the event stream. Interactive answers
// come from the desktop prompts, whichever client sent the message.
public sealed partial class MessageDispatcher(CompanionInstances instances, StateAggregation state, IpcSettings settings,
    IpcEvents events, IpcLogs logs, IStateFileSystem files, IPrompts prompts, ILauncher launcher,
    ICompanionDesktop desktop, IClock clock, HostAdministration hosts, CachedUpdateSource updates, IAudioCapture capture)
{
    public async Task DispatchAsync(string name, JsonObject message, CancellationToken ct)
    {
        var entry = instances.Get(name);
        var type = Text(message, "type");
        if (type.Length == 0) throw new IpcFailure(400, "invalidMessage", "A message type is required.");
        // A refresh only republishes; it must not queue behind a long-running command or prompt.
        var serialize = !IsRefresh(message);
        if (serialize) await entry.Serial.WaitAsync(ct);
        try
        {
            switch (type)
            {
                case "ready":
                    if (StateJson.Boolean(message["snapshotOnly"]) == true) state.PublishSnapshot(name);
                    else await RefreshAsync(entry, ct, bypassManifest: StateJson.Boolean(message["surfaceOpened"]) == true);
                    return;
                case "openPanel": await desktop.ActivateAsync(new("panel", name), ct); return;
                case "setInstance": await SelectAsync(Text(message, "name"), ct); return;
                case "setAudio":
                    if (StateJson.Boolean(message["enabled"]) is not bool enabled) throw new IpcFailure(400, "invalidMessage", "enabled must be a boolean.");
                    entry.Store.SaveState(new() { ["micPassthrough"] = enabled });
                    if (entry.Runtime is { } audioRuntime) await audioRuntime.SetAudioAsync(enabled, ct);
                    state.Publish(name, new { type = "settings", instance = name, settings = entry.Store.ReadSettings() }); return;
                case "saveSettings": await SaveSettings(entry, message["settings"] as JsonObject ?? throw new IpcFailure(400, "invalidSettings", "A settings object is required."), ct); return;
                case "customRebuild": RequireRebuild(message); await Lifecycle(entry, Text(message, "mode"), message, ct); return;
                case "applyVmResources": await ApplyVmResources(entry, ct); return;
                case "setUsagePeriod": entry.UsagePeriod = UsageParser.NormalizeReport(Text(message, "period")); await RefreshAsync(entry, ct); return;
                case "saveProject":
                    var project = Text(message, "name"); var profile = RequireProject(project, message["profile"]);
                    instances.Host.WriteProjectProfile(ProjectRoot(entry), project, JsonNode.Parse(ProfileCodec.CanonicalizeProfileText(project, profile.ToJsonString()).Content!)!.AsObject()); await RefreshAsync(entry, ct); return;
                case "saveIdlePolicy": await SaveIdle(entry, message["policy"] as JsonObject ?? [], ct); return;
                case "command": await Command(entry, message, ct); return;
                default: Refuse(name, type, "This message is not supported by Construct Companion."); return;
            }
        }
        catch (IpcFailure) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { logs.Failure("instance dispatch", e); Refuse(name, type == "command" ? Text(message, "id") : type, "The operation could not be completed. Check the instance connection and configuration."); }
        finally { if (serialize) entry.Serial.Release(); }
    }
    private async Task Command(CompanionInstance entry, JsonObject m, CancellationToken ct)
    {
        var name = entry.Name; var id = Text(m, "id");
        switch (id)
        {
            case "refresh": await RefreshAsync(entry, ct); break;
            case "chooseTheme": await desktop.ActivateAsync(new("theme", name), ct); break;
            case "chooseMicDevice":
                var devices = await capture.EnumerateDevicesAsync(ct);
                var pickedDevice = await prompts.PickAsync(new("Microphone device", [new("", "System default"), .. devices.Select(d => new PickItem(d.Id, d.Name))]), ct);
                if (pickedDevice?.FirstOrDefault() is {} device) settings.Merge(new() { ["micDevice"] = device });
                break;
            case "showLogs": await launcher.OpenAsync(logs.PathName, ct); break;
            case "connect": await Connect(entry, "/root/repos", ct); break;
            case "startConnect":
                if (instances.Remote(entry) is { } remote) await remote.PowerAsync(Text(entry.Definition, "vmName"), "start", ct);
                else await launcher.StartDetachedAsync(VmPower.BuildElevatedCommandLaunch(VmPower.BuildStartCommand(Text(entry.Definition, "vmName"))).Invocation(), ct);
                entry.Runtime?.BeginFastRefresh();
                for (var attempt = 0; attempt < 90; attempt++)
                {
                    var reply = await entry.Ssh.RunRemoteScriptAsync("true", TimeSpan.FromSeconds(8), ct);
                    if (reply.Code == 0) { await Connect(entry, "/root/repos", ct); return; }
                    await clock.DelayAsync(TimeSpan.FromSeconds(2), ct);
                }
                Refuse(name, id, "The VM did not become reachable. Refresh and connect when it is ready."); break;
            case "shutdown":
                if (await prompts.ConfirmAsync("Shutdown", "Shut down the Construct VM?", ct))
                {
                    var shutdown = await entry.Ssh.RunRemoteScriptAsync(VmPower.ShutdownCommand, TimeSpan.FromSeconds(20), ct);
                    events.Companion(new { type = "notification", level = shutdown.Code == 0 ? "info" : "warning", text = shutdown.Code == 0 ? "Shutdown command sent." : "Shutdown command sent; SSH disconnected or reported failure. Refresh to check the VM." });
                    entry.Runtime?.BeginFastRefresh();
                } break;
            case "reprovision": case "reinstall": case "redownload": case "exportConfig": await Lifecycle(entry, id, m, ct); break;
            case "openForward": case "closeForward":
                var forward = RequireForwardId(Text(m, "forward"));
                if (entry.Runtime?.Forwarder is not { } forwarder) { Refuse(name, id, "The forward is no longer active."); break; }
                if (id == "closeForward") { if (!await forwarder.CloseAsync(forward, ct)) Refuse(name, id, "The forward is no longer active."); }
                else
                {
                    var item = forwarder.Snapshot["items"]?.AsArray().OfType<JsonObject>().FirstOrDefault(x => Text(x, "id") == forward);
                    var link = item is null ? "" : Text(item, "url");
                    if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await launcher.OpenAsync(link, ct);
                    else Refuse(name, id, "The forward has no open link.");
                }
                break;
            case "openAgentWeb":
                if (Text(m, "agent") != "t3code") { Refuse(name, id, "Only T3 Code exposes a web UI."); break; }
                var pairing = await entry.Ssh.RunRemoteScriptAsync(T3Code.BuildPairingScript(entry.Definition), TimeSpan.FromSeconds(90), ct);
                var pairUrl = T3Code.ExtractPairUrl(pairing.Stdout);
                if (pairing.Code == 7) { Refuse(name, id, "T3 Code's port forward is not ready. Keep the Construct client connected and retry."); break; }
                if (pairing.Code != 0 || !Uri.TryCreate(pairUrl, UriKind.Absolute, out var pairUri) || pairUri.Scheme is not ("http" or "https")) Refuse(name, id, "T3 Code did not return a pairing link.");
                else await launcher.OpenAsync(pairUrl, ct); break;
            case "updateAgents": case "updateAgent":
                var agent = id == "updateAgent" ? RequireAgent(Text(m, "agent")) : null;
                await CheckedScript(entry, AgentUpdateScript.Build(agent is null ? null : [agent]), ct, TimeSpan.FromMinutes(10)); await RefreshAsync(entry, ct); break;
            case "openProjectFolder": await launcher.OpenAsync(Path.Combine(ProjectRoot(entry), "projects"), ct); break;
            case "openProject":
                var projectName = Text(m, "project");
                if (!instances.Host.ListProjectProfiles(ProjectRoot(entry)).Contains(projectName)) throw new IpcFailure(404, "projectNotFound", "Unknown project.");
                await Connect(entry, ProjectNavigation.OpenPath(instances.Host.ReadProjectProfile(ProjectRoot(entry), projectName)), ct); break;
            case "editProject":
                var profile = instances.Host.ReadProjectProfile(ProjectRoot(entry), Text(m, "project"));
                if (profile is null) throw new IpcFailure(404, "projectNotFound", "Unknown project.");
                events.Message(name, new { type = "editProject", name = Text(m, "project"), profile }); break;
            case "deleteProject":
                var delete = Text(m, "project");
                if (!instances.Host.ListProjectProfiles(ProjectRoot(entry)).Contains(delete)) throw new IpcFailure(404, "projectNotFound", "Unknown project.");
                if (await prompts.ConfirmAsync("Delete project", $"Delete the project profile \"{delete}\"? The repository on the VM is kept.", ct))
                { instances.Host.DeleteProjectProfile(ProjectRoot(entry), delete); entry.Store.SaveSelectedProjects(new JsonArray(entry.Store.ReadSelectedProjects().Where(n => StateJson.String(n) != delete).Select(n => n?.DeepClone()).ToArray())); await RefreshAsync(entry, ct); } break;
            case "selectProfiles":
                var selected = entry.Store.ReadSelectedProjects().Select(StateJson.String).ToHashSet();
                var picked = await prompts.PickAsync(new("Construct projects", instances.Host.ListProjectProfiles(ProjectRoot(entry)).Select(p => new PickItem(p, p, Picked: selected.Contains(p))).ToArray(), true), ct);
                if (picked is not null) { entry.Store.SaveSelectedProjects(JsonSerializer.SerializeToNode(instances.Host.ListProjectProfiles(ProjectRoot(entry)).Where(picked.Contains))); await RefreshAsync(entry, ct); } break;
            case "exportUsage":
                var path = await prompts.SaveFileAsync(new("Export token usage", "construct-usage.json", "JSON (*.json)|*.json"), ct);
                if (path is not null) files.WriteFileAtomic(path, Encoding.UTF8.GetBytes(UsageParser.BuildExportPayload(entry.UsageRaw, clock.UtcNow.ToString("O")))); break;
            case "openHostAdmin":
                var client = instances.Remote(entry);
                if (client is null) { Refuse(name, id, "This instance has no remote host service."); break; }
                var slug = RemoteHost.HostSlug(client.BaseUrl);
                await hosts.DispatchAsync(slug, new() { ["type"] = "hostadmin.ready" }, ct);
                await desktop.ActivateAsync(new("hostadmin", Host: slug), ct); break;
            case "childConsole": case "childShutdown": case "childDelete": await hosts.ChildActionAsync(entry, id, Text(m, "child"), ct); break;
            case "syncConfigNow": case "addConfigRemote": case "removeConfigRemote": case "importRemoteConfigs": case "shareConfigs": case "pushConfigUpstream": case "publishConfigProfiles": case "addRemoteAndPublish": case "openConfigRepo":
                await ConfigCommand(entry, id, m, ct); break;
            case "installGit": await launcher.StartDetachedAsync(PowerShellLaunch.BuildInstallGitLaunch().Invocation(), ct); break;
            // Documented unsupported workflows (companion/README.md): refused visibly, never ignored.
            case "registerThisVm": Refuse(name, id, "Registration requires an attached Remote-SSH window. Use Register this VM in VS Code."); break;
            case "addProject": Refuse(name, id, "Clone-and-register project creation is not yet ported. Save a project profile or use Add Project in VS Code fallback mode."); break;
            case "removeInstance": Refuse(name, id, "Instance removal needs the installer removal planner. Use Remove Instance in VS Code."); break;
            case "convertToHost": Refuse(name, id, "Host conversion requires the attached VM identity and explicit finish workflow. Review or finish it in VS Code; Companion never finishes a pending conversion automatically."); break;
            case "createFirstVm": Refuse(name, id, "The remote VM creation wizard is not yet ported. Use New Remote VM in VS Code."); break;
            case "updateConstruct": await UpdateConstruct(entry, ct); break;
            default: Refuse(name, id, "This command is not supported by Construct Companion."); break;
        }
    }
    // Recomputes and publishes the full state message (probe, host extras, usage, config sync,
    // update enrichment). The enrichment service calls it with probe:false and collectUsage:false.
    public async Task RefreshAsync(CompanionInstance entry, CancellationToken ct, bool probe = true, bool collectUsage = true, bool bypassManifest = false)
    {
        await entry.EnrichmentSerial.WaitAsync(ct);
        try
        {
            // Host inventory publishes independently while SSH status is still in flight.
            var extras = hosts.RefreshExtrasAsync(entry, ct);
            var status = probe && entry.Runtime is { } runtime ? runtime.ProbeOnceAsync(ct) : Task.CompletedTask;
            await Task.WhenAll(extras, status);
            if (collectUsage)
            {
                var period = entry.UsagePeriod;
                if (!entry.UsageCache.TryGetValue(period, out var cached) || !RefreshCachePolicy.Fresh("usage", cached.Raw is not null, (clock.UtcNow - cached.At).TotalMilliseconds))
                {
                    var usage = await entry.Ssh.RunRemoteScriptAsync(UsageParser.BuildUsageScript(period), TimeSpan.FromSeconds(60), ct);
                    cached = (clock.UtcNow, usage.Code == 0 && StateJson.ParseObject(usage.Stdout) is not null ? usage.Stdout : null);
                    entry.UsageCache[period] = cached;
                }
                entry.UsageRaw = cached.Raw; entry.Usage = cached.Raw is null ? null : UsageParser.ParseUsage(StateJson.ParseObject(cached.Raw));
            }
            if (entry.ConfigSync is { } configArea) entry.ConfigState = JsonSerializer.SerializeToNode(await configArea.Runtime.BuildStateAsync(ct), IpcJson.Options);
            var full = state.State(entry.Name);
            var data = full["state"]!.AsObject();
            var markers = entry.Store.ReadMarkers();
            data["provisionStale"] = UpdatePlanner.IsProvisionStale(markers, Text(data, "provisionedCommit"));
            data["constructUpdate"] = await UpdatePlanner.CheckConstructAsync(bypassManifest ? updates.Bypass() : updates, markers, ct);
            UpdatePlanner.Fold(data, markers, data["constructUpdate"] as JsonObject);
            if (data["agents"] is JsonArray agents) data["agents"] = await UpdatePlanner.AugmentAgentsAsync(updates, agents, ct);
            // Everything the enrichment adds must survive the next probe-driven rebuild of the state (StateAggregation.State copies Enrichment).
            entry.Enrichment = new JsonObject { ["constructUpdate"] = data["constructUpdate"]?.DeepClone(), ["provisionStale"] = data["provisionStale"]?.DeepClone() };
            foreach (var key in new[] { "update", "constructRev" }) if (data[key] is { } folded) entry.Enrichment[key] = folded.DeepClone();
            state.Publish(entry.Name, full);
            state.PublishSnapshot(entry.Name);
        }
        finally { entry.EnrichmentSerial.Release(); }
    }
    public Task SelectAsync(string name, CancellationToken ct)
    { instances.Get(name); settings.Merge(new() { ["activeInstance"] = name }); events.Companion(new { type = "activeInstance", instance = name }); return Task.CompletedTask; }
    private async Task SaveSettings(CompanionInstance entry, JsonObject form, CancellationToken ct)
    {
        var previous = entry.Store.ReadSettings(); entry.Store.SaveSettings(form); var merged = entry.Store.ReadSettings();
        state.Publish(entry.Name, new { type = "settings", instance = entry.Name, settings = merged });
        if (entry.Runtime is { } runtime) await runtime.SetAudioAsync(StateJson.Boolean(merged["mic"]) == true, ct);
        if (StateJson.Boolean(form["autoCheckpoints"]) is not null) await ApplyCheckpoints(entry, StateJson.Boolean(merged["autoCheckpoints"]) == true, ct);
        var changes = SettingsMapping.PatchReprovisionChanges(previous, merged);
        var t3 = T3Code.PlanLiveAction(StateJson.Boolean(merged["t3code"]) == true, StateJson.Boolean(previous["t3code"]) == true, Text(merged, "t3codeChannel"), Text(previous, "t3codeChannel"));
        if (t3 is not null && (Text(t3, "action") == "disable" || StateJson.Boolean(merged["t3codeLimitResume"]) != true))
            await CheckedScript(entry, Text(t3, "action") == "disable" ? T3Code.BuildDisableScript() : T3Code.BuildInstallScript(Text(t3, "channel")), ct, TimeSpan.FromMinutes(10));
        if (changes.Length > 0 && await prompts.ConfirmAsync("Reprovision required", "Saved settings need reprovisioning to take effect: " + string.Join(", ", changes) + ". Reprovision now?", ct)) await Lifecycle(entry, "reprovision", [], ct);
    }
    private async Task Lifecycle(CompanionInstance entry, string action, JsonObject message, CancellationToken ct)
    {
        try
        {
            var directory = RequireDirectory(entry);
            if (action is "reprovision" or "reinstall" or "redownload" && !await LifecyclePreflight(entry, action, ct)) return;
            var invocation = LifecycleBuilder.BuildInvocation(action, new()
            {
                ["instance"] = entry.Definition.DeepClone(), ["settings"] = entry.Store.ReadSettings(),
                ["instanceParams"] = JsonSerializer.SerializeToNode(LifecycleBuilder.InstanceParameterSupport(files, directory, action, entry.Definition)),
                ["projects"] = await EffectiveProjects(entry, ct), ["backupMode"] = message["backup"]?.DeepClone(),
                ["backupDir"] = Path.Combine(directory, "config")
            });
            if (invocation is null || StateJson.Boolean(invocation["blocked"]) == true) { Refuse(entry.Name, action, invocation is null ? "This lifecycle action is unavailable." : Text(invocation, "reason")); return; }
            if (StateJson.Boolean(invocation["destructive"]) == true && !await prompts.ConfirmAsync(Text(invocation, "label"), $"{Text(invocation, "label")} \"{entry.Name}\"? This deletes and recreates its VM.", ct)) return;
            var script = Path.Combine(directory, Text(invocation, "script"));
            if (!files.FileExists(script)) { Refuse(entry.Name, action, "The installed lifecycle script is missing."); return; }
            var args = invocation["args"]!.AsArray().Select(StateJson.String).ToArray();
            var launch = PowerShellLaunch.BuildHostLaunch(script, args, elevate: StateJson.Boolean(invocation["elevate"]) == true, keepOpen: settings.Read().Debug, argSpec: invocation["argSpec"] as JsonArray).Invocation(directory);
            if (StateJson.Boolean(invocation["elevate"]) == true) await launcher.LaunchElevatedAsync(launch, ct); else await launcher.StartDetachedAsync(launch, ct);
            entry.Runtime?.BeginFastRefresh(); events.Companion(new { type = "lifecycle", instance = entry.Name, action, status = "launched" });
        }
        // The panel keeps its spinner until lifecyclePrepared arrives, whatever happened above.
        finally { events.Message(entry.Name, new { type = "lifecyclePrepared", id = Text(message, "type") == "customRebuild" ? action == "redownload" ? "customRedownload" : "customReinstall" : action }); }
    }
    // Update-Construct.ps1 is install-wide: it refreshes the scripts, the VS Code extension and this Companion
    // (its hook asks this process to quit and restarts it), so the console runs detached and the outcome is read
    // from the result file exactly as the extension does. A completed update usually ends this process first.
    private async Task UpdateConstruct(CompanionInstance entry, CancellationToken ct)
    {
        try
        {
            var directory = RequireDirectory(entry);
            var script = Path.Combine(directory, "Update-Construct.ps1");
            if (!files.FileExists(script)) { Refuse(entry.Name, "updateConstruct", "Update-Construct.ps1 is missing from the installed scripts."); return; }
            var markers = entry.Store.ReadMarkers();
            var plan = ResultPollingPlan.Create(files.GetRoot(FileSystemRoot.Temp) ?? directory, "update", clock.UtcNow.ToUnixTimeMilliseconds());
            if (files.FileExists(plan.File)) files.DeleteFile(plan.File);
            var argSpec = new JsonArray(new JsonObject { ["flag"] = "-Repo", ["value"] = StateJson.String(markers["repo"]) }, new JsonObject { ["flag"] = "-Ref", ["value"] = StateJson.String(markers["ref"]) });
            var launch = PowerShellLaunch.BuildHostLaunch(script, UpdatePlanner.ConstructRefreshArgs(markers), elevate: false, keepOpen: settings.Read().Debug, argSpec: argSpec).Invocation(directory)
                with { EnvironmentOverrides = new Dictionary<string, string?> { [plan.EnvironmentKey] = plan.File } };
            await launcher.StartDetachedAsync(launch, ct);
            events.Companion(new { type = "lifecycle", instance = entry.Name, action = "updateConstruct", status = "launched" });
            _ = WatchUpdateResultAsync(entry.Name, plan);
        }
        finally { events.Message(entry.Name, new { type = "lifecyclePrepared", id = "updateConstruct" }); }
    }
    private async Task WatchUpdateResultAsync(string name, ResultPollingPlan plan)
    {
        try
        {
            var outcome = await PollResultAsync(files, clock, plan, CancellationToken.None);
            events.Companion(new { type = "lifecycle", instance = name, action = "updateConstruct", status = outcome });
            if (outcome == "fail") Refuse(name, "updateConstruct", "Construct update didn't complete. See the update console, then retry.");
            else if (outcome == "timeout") logs.Failure("Construct update", new TimeoutException("No result was written within the update timeout; the console shows the outcome."));
        }
        catch (Exception e) { logs.Failure("Construct update", e); }
    }
    public static async Task<string> PollResultAsync(IStateFileSystem files, IClock clock, ResultPollingPlan plan, CancellationToken ct)
    {
        var started = clock.UtcNow;
        while (true)
        {
            await clock.DelayAsync(plan.Interval, ct);
            var text = files.ReadFile(plan.File) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;
            var outcome = plan.Evaluate(text, clock.UtcNow - started);
            if (outcome == "pending") continue;
            if (files.FileExists(plan.File)) files.DeleteFile(plan.File);
            return outcome;
        }
    }
    private async Task SaveIdle(CompanionInstance entry, JsonObject policy, CancellationToken ct)
    {
        var client = instances.Remote(entry) ?? throw new IpcFailure(409, "localInstance", "Idle policy is enforced by the remote host service.");
        var route = "/vms/" + RemoteHost.Encode(Text(entry.Definition, "vmName")) + "/idle-policy";
        var current = await client.RequestAsync("GET", route, cancellationToken: ct);
        var wanted = HostAdminProtocol.ClampIdlePolicy(policy, StateJson.Number(current?["maxTimeoutMinutes"]) ?? 0);
        wanted.Remove("clamped");
        var applied = await client.RequestAsync("PUT", route, wanted, ct);
        state.Publish(entry.Name, new { type = "idlePolicy", instance = entry.Name, idlePolicy = HostAdminProtocol.IdlePolicy(applied as JsonObject) });
    }
    private async Task ConfigCommand(CompanionInstance entry, string id, JsonObject message, CancellationToken ct)
    {
        var area = entry.ConfigSync ?? throw new IpcFailure(409, "configUnavailable", "Config sync is not configured for this instance.");
        if (id == "openConfigRepo") { await launcher.OpenAsync(area.Repository.Directory, ct); return; }
        if (id == "syncConfigNow") { await area.Runtime.SyncNowAsync(ct); await area.Actions.ImportAsync(ct); }
        else
        {
            var result = id switch
            {
                "addConfigRemote" => await area.Actions.AddRemoteAsync(false, ct),
                "addRemoteAndPublish" => await area.Actions.AddRemoteAsync(true, ct),
                "removeConfigRemote" => await area.Actions.RemoveRemoteAsync(Text(message, "url"), ct),
                "importRemoteConfigs" => await area.Actions.ImportAsync(ct),
                "shareConfigs" => await area.Actions.ShareAsync(Text(entry.Store.ReadInstallWide(), "constructRepo"), Text(entry.Store.ReadInstallWide(), "constructRef"), ct),
                "pushConfigUpstream" => await area.Actions.PushUpstreamAsync(Text(message, "url"), ct),
                _ => await area.Actions.PublishAsync(Text(message, "url"), ct)
            };
            if (!result.Ok) Refuse(entry.Name, id, result.Message);
            else if (id == "importRemoteConfigs") await area.Runtime.SyncNowAsync(ct);
        }
        entry.ConfigState = JsonSerializer.SerializeToNode(await area.Runtime.BuildStateAsync(ct), IpcJson.Options); var full = state.State(entry.Name); full["state"]!["configSync"] = entry.ConfigState?.DeepClone(); events.Message(entry.Name, full);
    }

    // The HTTP route answers 400 synchronously from these checks; DispatchAsync repeats the
    // ones it needs for values it uses, so a message that skipped Validate still cannot act on bad input.
    public void Validate(string name, JsonObject message)
    {
        instances.Get(name);
        var type = Text(message, "type"); var id = Text(message, "id");
        if (type.Length == 0 || type == "command" && id.Length == 0) throw new IpcFailure(400, "invalidMessage", "A message type and command id are required.");
        if (type == "setAudio" && StateJson.Boolean(message["enabled"]) is null) throw new IpcFailure(400, "invalidMessage", "enabled must be a boolean.");
        if (type == "saveSettings" && message["settings"] is not JsonObject) throw new IpcFailure(400, "invalidSettings", "A settings object is required.");
        if (type == "setInstance") instances.Get(Text(message, "name"));
        if (type == "customRebuild") RequireRebuild(message);
        if (type == "saveProject") RequireProject(Text(message, "name"), message["profile"]);
        if (type == "command" && id is "editProject" or "deleteProject" && !EditableProject(Text(message, "project"))) throw new IpcFailure(400, "invalidProject", "The project name is invalid or reserved.");
        if (type == "command" && id is "openForward" or "closeForward") RequireForwardId(Text(message, "forward"));
        if (type == "command" && id == "updateAgent") RequireAgent(Text(message, "agent"));
    }
    public static bool IsRefresh(JsonObject message) => Text(message, "type") == "ready" || Text(message, "type") == "command" && Text(message, "id") == "refresh";
    private static bool EditableProject(string name) => HostState.SafeProfileName(name).Length > 0 && name is not ("default" or "project.schema");
    private static void RequireRebuild(JsonObject message)
    {
        if (Text(message, "mode") is not ("reinstall" or "redownload") || Text(message, "backup") is not ("save" or "existing" or "wipe")) throw new IpcFailure(400, "invalidRebuild", "Invalid rebuild mode or backup selection.");
    }
    private static JsonObject RequireProject(string name, JsonNode? profile)
    {
        if (profile is not JsonObject value || !EditableProject(name) || ProfileCodec.ValidateProfile(name, value).Count > 0) throw new IpcFailure(400, "invalidProject", "The project profile is invalid or reserved.");
        return value;
    }
    private static string RequireForwardId(string forward) => ForwardProtocol.IsSafeId(forward) ? forward : throw new IpcFailure(400, "invalidForward", "Invalid forward id.");
    private static string RequireAgent(string agent) => agent is "claude-code" or "codex" or "opencode" or "t3code" ? agent : throw new IpcFailure(400, "invalidAgent", "Unknown agent.");

    private Task Connect(CompanionInstance entry, string path, CancellationToken ct) => launcher.OpenAsync("vscode://vscode-remote/ssh-remote+" + Uri.EscapeDataString(Text(entry.Definition, "hostAlias")) + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), ct);
    private string ProjectRoot(CompanionInstance entry) => instances.Host.ConfigDirectory ?? RequireDirectory(entry);
    private static string RequireDirectory(CompanionInstance entry) => entry.Store.ScriptsDirectory ?? throw new IpcFailure(409, "scriptsUnavailable", "No Construct scripts directory resolved.");
    private static async Task CheckedScript(CompanionInstance entry, string script, CancellationToken ct, TimeSpan? timeout = null)
    { if ((await entry.Ssh.RunRemoteScriptAsync(script, timeout ?? TimeSpan.FromSeconds(30), ct)).Code != 0) throw new IpcFailure(502, "guestOperationFailed", "The guest operation failed."); }
    // lifecyclePrepared with an error is the one envelope every panel surface already renders as a refusal.
    public void Refuse(string name, string id, string reason) => events.Message(name, new { type = "lifecyclePrepared", id, error = reason });
    // "" for a missing or non-string field, as the JS `String(m.x || "")` reads.
    internal static string Text(JsonObject value, string key) => StateJson.Text(value[key]) ?? "";
}
