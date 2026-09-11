using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
namespace Construct.Companion.Host.Dispatch;

public sealed partial class MessageDispatcher
{
    private async Task RegisterThisVm(CancellationToken ct)
    {
        var host = (await prompts.InputAsync(new("Register this VM as a Construct instance", "SSH host of the VM to register"), ct))?.Trim();
        if (string.IsNullOrEmpty(host)) return;
        if (!Instances.IsHostEndpoint(host)) { Notify("Enter a hostname or IP address.", "warning"); return; }
        var suggested = host.Split('.')[0].ToLowerInvariant();
        if (!Instances.IsValidName(suggested)) suggested = "";
        var name = (await prompts.InputAsync(new("Register this VM as a Construct instance", $"The VM at {host} is recorded under this name, and the panel switches to it.", suggested), ct))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var registry = instances.Registry; var plan = InstanceWorkflowPlans.Register(registry, name, host);
        if (StateJson.Boolean(plan["ok"]) != true) { Notify(Text(plan, "reason"), "warning"); return; }
        registry.Add(name, plan["entry"]!.AsObject()).Save(files);
        await SelectAsync(name, ct); events.Companion(new { type = "instances" });
        Notify($"Registered \"{name}\" — the panel now describes this VM.");
    }
    private async Task RemoveInstance(CompanionInstance entry, CancellationToken ct)
    {
        var directory = RequireDirectory(entry); var plan = InstanceWorkflowPlans.Remove(instances.Registry, entry.Name);
        if (StateJson.Boolean(plan["ok"]) != true && StateJson.Boolean(plan["requiresTypedConfirmation"]) != true) { Notify(Text(plan, "refusal"), "warning"); return; }
        var script = Path.Combine(directory, "Auto-Install.ps1");
        var source = files.ReadFile(script) is {} bytes ? System.Text.Encoding.UTF8.GetString(bytes) : "";
        if (!LifecycleBuilder.ScriptSupportsRemoveInstance(source))
        { Notify("This install's Auto-Install.ps1 does not know -Action remove-instance. Update Construct first.", "warning"); return; }
        var confirmation = ""; var keepVm = false;
        if (StateJson.Boolean(plan["requiresTypedConfirmation"]) == true)
        {
            var choice = await prompts.PickAsync(new($"Remove the instance \"{entry.Name}\"", [new("delete", "Delete VM and remove instance"), new("keep", "Keep VM; remove from this PC only")]), ct);
            if (choice?.FirstOrDefault() is not {} selected) return;
            keepVm = selected == "keep";
            if (keepVm && !LifecycleBuilder.ScriptSupportsParameter(source, "KeepVm")) { Notify("This install's Auto-Install.ps1 does not accept -KeepVm. Update Construct first.", "warning"); return; }
            confirmation = (await prompts.InputAsync(new($"Remove the instance \"{entry.Name}\"", keepVm ? $"The VM stays on its host service. Type \"{entry.Name}\" to remove this PC's client state."
                : $"This DELETES the VM \"{entry.Name}\" on its host service, including its disk. Type the instance name to confirm."), ct))?.Trim() ?? "";
            if (confirmation != entry.Name) return;
        }
        else if (!await prompts.ConfirmAsync(new ConfirmationPrompt($"Remove \"{entry.Name}\" from this PC?", string.Join('\n', plan["removes"]!.AsArray().Select(n => "• " + StateJson.String(n))) + "\n\n" + string.Join('\n', plan["keeps"]!.AsArray().Select(StateJson.String)), "Remove instance"), ct)) return;
        var invocation = LifecycleBuilder.BuildInvocation("removeInstance", new() { ["instance"] = entry.Definition.DeepClone(), ["confirmation"] = confirmation })!;
        var args = invocation["args"]!.AsArray().Select(StateJson.String).ToList();
        var argSpec = invocation["argSpec"] as JsonArray;
        if (keepVm) { args.Add("-KeepVm"); argSpec?.Add(new JsonObject { ["flag"] = "-KeepVm" }); }
        await launcher.StartDetachedAsync(PowerShellLaunch.BuildHostLaunch(script, args, elevate: false, keepOpen: settings.Read().Debug, argSpec: argSpec).Invocation(directory), ct);
        // The registry watcher owns teardown/retargeting once the detached installer writes its result.
        events.Companion(new { type = "instances" }); await RefreshAsync(entry, ct, collectUsage: false);
    }
    private async Task AddProject(CompanionInstance entry, CancellationToken ct)
    {
        var url = (await prompts.InputAsync(new("Add project — clone a git repo onto the Construct VM", "Git URL to clone into /root/repos on the VM"), ct))?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        if (!InstanceWorkflowPlans.IsGitUrl(url)) { Notify("Enter an https://, ssh:// or git@host:path git URL.", "warning"); return; }
        // A URL credential would become process argv inside the SSH script. Use the guest's Git credential helper.
        if (ConfigSyncRules.UrlHasCredentials(url)) { Notify("Remove the credentials from the URL -- let your git credential helper supply them.", "warning"); return; }
        var name = InstanceWorkflowPlans.RepoName(url);
        if (name is "" or "." or "..") { Notify("Couldn't derive a folder name from that URL.", "warning"); return; }
        Notify($"Cloning {name} onto the VM{InstanceLabel(entry)}…");
        var result = await entry.Ssh.RunRemoteScriptAsync(InstanceWorkflowPlans.CloneScript(url, name), TimeSpan.FromMinutes(5), ct);
        if (result.Code == 0) { Notify($"Cloned {name} — opening it on the VM…"); await Connect(entry, "/root/repos/" + name, ct); await RefreshAsync(entry, ct, collectUsage: false); }
        else if (result.Code == 3)
        { if (await prompts.ConfirmAsync(new ConfirmationPrompt($"/root/repos/{name} already exists on the VM.", "", "Open it"), ct)) await Connect(entry, "/root/repos/" + name, ct); }
        else if (result.Code < 0) Notify("Couldn't reach the VM to clone. Is it running?", "warning");
        else Notify($"Cloning {name} failed (exit {result.Code}).", "warning"); // Never echo remote Git diagnostics containing a credential.
    }
}
