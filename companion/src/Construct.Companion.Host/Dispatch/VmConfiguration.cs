using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
namespace Construct.Companion.Host.Dispatch;

public sealed partial class MessageDispatcher
{
    private void Notify(string text, string level = "info") => events.Companion(new { type = "notification", level, text });
    private string InstanceLabel(CompanionInstance entry) => instances.Names.Length > 1 ? $" ({entry.Name})" : "";
    private JsonObject ResourcePlan(CompanionInstance entry) => VmResourcePlan.Create(entry.Store.ReadSettings(), state.State(entry.Name)["state"]?["vmSpec"] as JsonObject);
    private async Task ApplyVmResources(CompanionInstance entry, CancellationToken ct)
    {
        var directory = RequireDirectory(entry); var plan = ResourcePlan(entry);
        if (StateJson.Boolean(plan["none"]) == true) { Notify("Set a RAM size and/or a vCPU count under Settings → VM resources and save first — there is nothing to apply.", "warning"); return; }
        if (StateJson.Boolean(plan["pending"]) == false) { Notify($"The Construct VM already has {Text(plan, "current")} — nothing to apply."); return; }
        var backend = Text(entry.Definition, "backend");
        if (Instances.IsRemoteBackend(backend)) { await ApplyRemoteResources(entry, plan, ct); return; }
        if (backend is not ("" or "hyperv-local")) { Notify($"The “{backend}” backend can't be resized from here. Reinstall the VM to change its size.", "warning"); return; }
        if (!files.FileExists(Path.Combine(directory, "Set-AgentVmResources.ps1"))) { Notify("This Construct install's host scripts are too old to resize the VM in place. Update Construct first, or Reinstall to apply the saved size.", "warning"); return; }
        var summary = Text(plan, "summary"); var current = Text(plan, "current");
        var detail = $"The VM shuts down, is resized to {summary}, and starts again" + (current.Length > 0 ? $" (it has {current} now)." : ".") + " Needs administrator rights (a UAC prompt). The disk size is not changed by this — it still needs a Reinstall / Redownload.";
        if (!await prompts.ConfirmAsync(new ConfirmationPrompt($"Restart the Construct VM{InstanceLabel(entry)} with {summary}?", detail, "Restart & apply"), ct)) return;
        var fresh = ResourcePlan(entry);
        if (!JsonNode.DeepEquals(fresh["ram"], plan["ram"]) || !JsonNode.DeepEquals(fresh["cpu"], plan["cpu"]))
        { Notify("The VM-resources settings were changed elsewhere while this prompt was open — nothing was applied. Try again.", "warning"); return; }
        var polling = await LaunchVmConfiguration(entry, "setResources", new() { ["ram"] = plan["ram"]?.DeepClone(), ["cpu"] = plan["cpu"]?.DeepClone() }, ct);
        if (polling is null) return;
        var started = clock.UtcNow; var poweroffSent = false; string outcome;
        while (true)
        {
            await clock.DelayAsync(polling.Interval, ct);
            var result = files.ReadFile(polling.File) is {} bytes ? Encoding.UTF8.GetString(bytes).Trim() : null;
            if (result == "running" && !poweroffSent)
            {
                poweroffSent = true;
                try { await entry.Ssh.RunRemoteScriptAsync(VmPower.ShutdownCommand, TimeSpan.FromSeconds(20), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* Best effort: the elevated script also asks Hyper-V. */ }
            }
            outcome = polling.Evaluate(result, clock.UtcNow - started);
            if (outcome != "pending") break;
        }
        if (files.FileExists(polling.File)) files.DeleteFile(polling.File);
        if (outcome == "ok") { Notify($"The Construct VM{InstanceLabel(entry)} now has {summary} and is starting again."); await WaitForVmOnline(entry, ct); }
        else
        {
            if (outcome == "timeout") logs.Write("resources: timed out waiting for a result (UAC declined, or the console is still open)");
            if (outcome == "fail") Notify("Resizing the Construct VM didn't complete — see the console window for the error. If the VM stayed off, use “Start & connect”.", "warning");
            await RefreshAsync(entry, ct, collectUsage: false);
        }
    }
    private async Task<ResultPollingPlan?> LaunchVmConfiguration(CompanionInstance entry, string action, JsonObject options, CancellationToken ct)
    {
        var directory = RequireDirectory(entry);
        options["instance"] = entry.Definition.DeepClone();
        options["instanceParams"] = JsonSerializer.SerializeToNode(LifecycleBuilder.InstanceParameterSupport(files, directory, action, entry.Definition));
        var invocation = LifecycleBuilder.BuildInvocation(action, options);
        if (invocation is null || StateJson.Boolean(invocation["blocked"]) == true) { Refuse(entry.Name, action, invocation is null ? "This lifecycle action is unavailable." : Text(invocation, "reason")); return null; }
        var plan = ResultPollingPlan.Create(files.GetRoot(FileSystemRoot.Temp) ?? directory, action, clock.UtcNow.ToUnixTimeMilliseconds());
        if (files.FileExists(plan.File)) files.DeleteFile(plan.File);
        var launch = PowerShellLaunch.BuildHostLaunch(Path.Combine(directory, Text(invocation, "script")), invocation["args"]!.AsArray().Select(StateJson.String).ToArray(),
            elevate: true, keepOpen: settings.Read().Debug, argSpec: invocation["argSpec"] as JsonArray).Invocation(directory)
            with { EnvironmentOverrides = new Dictionary<string, string?> { [plan.EnvironmentKey] = plan.File } };
        await launcher.LaunchElevatedAsync(launch, ct);
        return plan;
    }
    private async Task ApplyCheckpoints(CompanionInstance entry, bool enabled, CancellationToken ct)
    {
        var actual = await instances.AutomaticCheckpointPolicyAsync(entry, ct);
        if (actual is "absent" or "unsupported" || actual == "on" && enabled || actual == "off" && !enabled || actual == "unknown" && entry.Store.ReadAppliedAutoCheckpoints() == enabled) return;
        if (!files.FileExists(Path.Combine(RequireDirectory(entry), "Set-AgentVmCheckpoints.ps1")))
        { Notify("Saved, but this Construct install's host scripts are too old to change automatic checkpoints (here or during a rebuild). Update Construct first.", "warning"); return; }
        var detail = enabled
            ? "Hyper-V will snapshot the VM at every start. This applies from the VM's next start; it's also used when the VM is rebuilt."
            : "Hyper-V will stop snapshotting the VM at every start, and the automatic checkpoint it already took will be removed (its disk is merged back in the background). Checkpoints Hyper-V doesn't report as automatic are never removed on their own — the console asks about each one separately first.";
        if (!await prompts.ConfirmAsync(new ConfirmationPrompt($"Apply automatic checkpoints = {(enabled ? "on" : "off")} to the current VM now?", detail + " Needs administrator rights (a UAC prompt).", "Apply now"), ct)) return;
        var wanted = StateJson.Boolean(entry.Store.ReadSettings()["autoCheckpoints"]) == true;
        if (wanted != enabled) { Notify($"Automatic checkpoints were changed to {(wanted ? "on" : "off")} elsewhere while this prompt was open — nothing was applied. Save again to apply the current setting.", "warning"); return; }
        var polling = await LaunchVmConfiguration(entry, "setCheckpoints", new() { ["enabled"] = enabled }, ct);
        if (polling is null) return;
        var outcome = await PollResultAsync(files, clock, polling, ct);
        if (outcome == "ok") { entry.Store.SaveAppliedAutoCheckpoints(enabled); Notify($"Automatic checkpoints are now {(enabled ? "on" : "off")} on the Construct VM."); }
        else if (outcome == "fail") Notify("Changing automatic checkpoints didn't complete — see the console window for the error.", "warning");
        else logs.Write("checkpoints: timed out waiting for a result (UAC declined, or the console is still open)");
    }
    private async Task WaitForVmOnline(CompanionInstance entry, CancellationToken ct)
    {
        entry.Runtime?.BeginFastRefresh();
        Notify("Waiting for the Construct VM to come back online…");
        for (var waited = 0; waited < 240000; waited += 4000)
        {
            if ((await entry.Ssh.RunRemoteScriptAsync("true", TimeSpan.FromSeconds(6), ct)).Code == 0) { await RefreshAsync(entry, ct, collectUsage: false); return; }
            await clock.DelayAsync(TimeSpan.FromSeconds(4), ct);
        }
        Notify("The VM didn't come back online in time. Once it's up, use “Open on VM”.", "warning");
        await RefreshAsync(entry, ct, collectUsage: false);
    }
    private async Task ApplyRemoteResources(CompanionInstance entry, JsonObject plan, CancellationToken ct)
    {
        var hostName = StateJson.Text(entry.Definition["service"]?["url"]) ?? "the host service";
        if (plan["cpu"] is null) { Notify($"The RAM of “{entry.Name}” can't be changed through {hostName}: the host service resizes only the vCPU count. A new RAM size needs a Reinstall.", "warning"); return; }
        var cpu = StateJson.String(plan["cpu"]); var ramNote = plan["ram"] is null ? "" : " The RAM size is NOT changed by the host service — it still needs a Reinstall.";
        var plural = cpu == "1" ? "" : "s";
        if (!await prompts.ConfirmAsync(new ConfirmationPrompt($"Restart “{entry.Name}” with {cpu} vCPU{plural}?", $"{hostName} records the new count, shuts the VM down gracefully and starts it again with it.{ramNote}", "Restart & apply"), ct)) return;
        var client = instances.Remote(entry);
        if (client is null) { Refuse(entry.Name, "applyVmResources", $"Couldn't reach {hostName}: no host service is recorded for this instance."); return; }
        try
        {
        var name = Text(entry.Definition, "vmName");
        var saved = await client.SetVmCpuAsync(name, new JsonObject { ["cpus"] = plan["cpu"]!.DeepClone() }, ct);
        if (StateJson.Boolean(saved?["pending"]) == false) { Notify($"“{entry.Name}” already has {cpu} vCPUs on {new Uri(client.BaseUrl).Host} — nothing to apply."); return; }
        async Task<string> RawState() => (StateJson.Text((await client.GetStateAsync(name, ct))?["state"]) ?? "").Trim().ToLowerInvariant();
        var vmState = await RawState();
        if (vmState is "saved" or "paused")
        {
            await client.LifecycleAsync(name, new JsonObject { ["action"] = "start" }, ct);
            for (var i = 0; i < 60 && vmState != "running"; i++) { await clock.DelayAsync(TimeSpan.FromSeconds(2), ct); vmState = await RawState(); }
            if (vmState != "running") throw new InvalidOperationException($"the VM did not resume (state: {(vmState.Length == 0 ? "unknown" : vmState)})");
        }
        if (vmState == "running")
        {
            var result = await client.LifecycleAsync(name, new JsonObject { ["action"] = "restart" }, ct);
            var id = StateJson.Text(result?["jobId"]);
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("the service accepted the restart without a job id");
            var job = await AwaitJob(client, id, ct);
            var status = (StateJson.Text(job?["state"]) ?? "").ToLowerInvariant();
            if (status != "succeeded") throw new InvalidOperationException($"the restart job ended {(status.Length == 0 ? "without a result" : status)}" + (StateJson.Text(job?["error"]) is { Length: > 0 } error ? $" ({error})" : ""));
        }
        else if (vmState == "off") await client.LifecycleAsync(name, new JsonObject { ["action"] = "start" }, ct);
        else throw new InvalidOperationException($"the VM's state is {(vmState.Length == 0 ? "unknown" : vmState)}; the count is recorded and applies on its next start");
        Notify($"“{entry.Name}” restarted with {cpu} vCPU{plural}.{ramNote}");
        await WaitForVmOnline(entry, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Refuse(entry.Name, "applyVmResources", $"Couldn't apply the vCPU count on {client.Host}: {await client.RedactDiagnosticAsync(error.Message, ct)}");
            await RefreshAsync(entry, ct, collectUsage: false);
        }
    }
    private async Task<JsonNode?> AwaitJob(RemoteHostClient client, string id, CancellationToken ct)
    {
        JsonNode? job = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            job = await client.GetJobAsync(id, ct); var status = (StateJson.Text(job?["state"]) ?? "").ToLowerInvariant();
            if (status is "succeeded" or "failed" or "cancelled" or "canceled") return job;
            await clock.DelayAsync(TimeSpan.FromSeconds(2), ct);
        }
        return job;
    }
}
