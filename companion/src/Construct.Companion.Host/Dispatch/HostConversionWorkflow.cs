using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using HostIdentity = Construct.Companion.Core.Remote.RemoteHost;
namespace Construct.Companion.Host.Dispatch;

public sealed class HostConversionWorkflow(CompanionInstances instances, IStateFileSystem files, ITokenStore tokens, IRemoteApi api,
    IHostConversionCrypto crypto, IProcessRunner processes, IPrompts prompts, ILauncher launcher, IClock clock, IpcEvents events, HostAdministration hosts, StateAggregation state)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private void Notice(string text) => events.Companion(new { type = "notification", text });
    public async Task RunAsync(CompanionInstance entry, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try { await Run(entry, ct); }
        finally { serial.Release(); }
    }
    private async Task Run(CompanionInstance entry, CancellationToken ct)
    {
        var path = HostConversion.PendingPath(instances.Host.LocalAppData);
        var plan = StateJson.ReadObject(files, path);
        if (files.FileExists(path) && plan is null) throw new IpcFailure(409, "invalidConversion", "The pending host conversion record could not be read.");
        if (plan is not null && StateJson.Text(plan["name"]) != entry.Name) throw new IpcFailure(409, "pendingConversion", $"Finish the pending host setup for {StateJson.Text(plan["name"])} first.");
        if (plan is not null && ReadResult(plan) is {} finished && StateJson.Boolean(finished["ok"]) == true)
        {
            if (await prompts.ConfirmAsync(new ConfirmationPrompt("Finish host conversion", $"Host installation finished for {entry.Name}. Finish conversion to verify access and connect VS Code.", "Finish host conversion"), ct))
            { Notice("Finishing host conversion…"); await Complete(plan, finished, ct); }
            return;
        }
        if (StateJson.Text(entry.Definition["backend"]) != "hyperv-local") throw new IpcFailure(409, "conversionUnavailable", "Connect to this PC's local Construct VM to convert it.");
        var directory = entry.Store.ScriptsDirectory ?? throw new IpcFailure(409, "scriptsUnavailable", "No Construct scripts directory resolved.");
        if (!files.FileExists(Path.Combine(directory, "service", "host", "ConvertTo-ConstructHost.ps1"))) throw new IpcFailure(409, "scriptsUnavailable", "Update Construct first: ConvertTo-ConstructHost.ps1 is missing.");
        if (plan is null)
        {
            var machine = await entry.Ssh.RunRemoteScriptAsync("cat /etc/machine-id", TimeSpan.FromSeconds(30), ct);
            if (machine.Code != 0 || !Regex.IsMatch(machine.Stdout.Trim(), "^[a-f0-9]{32}$")) throw new IpcFailure(409, "vmOffline", "The VM must answer SSH before conversion.");
            var pcResult = await processes.RunAsync(HostConversionLaunch.MachineIdentity(), ct);
            var pc = StateJson.ParseObject(pcResult.Stdout);
            if (pcResult.Code != 0 || pc is null || StateJson.Text(pc["adminUser"]) is not { Length: > 0 }) throw new IpcFailure(409, "hostIdentity", "The Windows host identity could not be read.");
            var publicHost = StateJson.Text(pc["hostName"]) ?? "";
            if (!HostConversionLaunch.ValidHost(publicHost)) publicHost = StateJson.Text(pc["ip"]) ?? "";
            if (!HostConversionLaunch.ValidHost(publicHost)) throw new IpcFailure(409, "hostIdentity", "The Windows host address could not be read.");
            var dns = await entry.Ssh.RunRemoteScriptAsync("getent ahostsv4 " + Construct.Companion.Core.ShellQuote.Single(publicHost), TimeSpan.FromSeconds(30), ct);
            if (dns.Code != 0 && HostConversionLaunch.ValidHost(StateJson.Text(pc["ip"]))) publicHost = StateJson.String(pc["ip"]);
            var home = files.GetRoot(FileSystemRoot.UserProfile) ?? throw new IpcFailure(409, "profileUnavailable", "The Windows user profile could not be found.");
            var keyPath = Path.Combine(home, ".ssh", StateJson.String(entry.Definition["keyName"]));
            if (!files.FileExists(keyPath)) throw new IpcFailure(409, "keyUnavailable", "The local VM's saved SSH key could not be found.");
            plan = new JsonObject { ["name"] = entry.Name, ["vmName"] = entry.Definition["vmName"]!.DeepClone(), ["scriptsDir"] = directory,
                ["adminUser"] = pc["adminUser"]!.DeepClone(), ["publicHost"] = publicHost, ["keepAwake"] = false, ["machineId"] = machine.Stdout.Trim(),
                ["sshHost"] = entry.Definition["vmHost"]!.DeepClone(), ["sshPort"] = entry.Definition["sshPort"]!.DeepClone(), ["keyPath"] = keyPath, ["knownHosts"] = Path.Combine(home, ".ssh", "known_hosts"),
                ["fingerprint"] = Instances.TargetFingerprint(entry.Definition), ["ubuntu"] = StateJson.Text(entry.Store.ReadInstallWide()["ubuntuRelease"]) ?? "24.04" };
        }
        var previous = ReadResult(plan); var locked = StateJson.Boolean(previous?["hostInstalled"]) == true;
        if (previous is not null) Notice(StateJson.Text(previous["error"]) ?? "Host setup stopped. Review and retry setup.");
        if (!locked)
        {
            var address = await prompts.InputAsync(new("Make this PC a Construct host", "Address other users will connect to", StateJson.Text(plan["publicHost"])), ct);
            if (address is null) return;
            if (!HostConversionLaunch.ValidHost(address.Trim())) { Notice("Enter a hostname or IPv4 address."); return; }
            plan["publicHost"] = address.Trim();
            var awake = await prompts.PickAsync(new("Keep this PC awake while plugged in", [new("no", "Retain the current power settings", Picked: StateJson.Boolean(plan["keepAwake"]) != true), new("yes", "Keep this PC awake while plugged in", Picked: StateJson.Boolean(plan["keepAwake"]) == true)], Placeholder: "The host also prevents idle sleep while managed VMs run."), ct);
            if (awake?.FirstOrDefault() is not {} selected) return;
            plan["keepAwake"] = selected == "yes";
        }
        if (!await prompts.ConfirmAsync(new ConfirmationPrompt("Make this PC a Construct host", $"Install the host service and adopt {entry.Name} with its existing data and settings.\nHost administrator: {StateJson.Text(plan["adminUser"])}\nYour VM stays private. Add other users from Host administration after setup.\nSetup opens an administrator PowerShell window. After Windows asks for approval, installation and VM conversion run automatically. Your VM remains running.", plan["id"] is null ? "Install host and adopt VM" : "Retry setup"), ct)) return;
        if (!instances.Registry.ByName.TryGetValue(entry.Name, out var current) || Instances.TargetFingerprint(current) != StateJson.Text(plan["fingerprint"])) throw new IpcFailure(409, "instanceChanged", "The instance changed during setup. Review its identity and try again.");
        if (plan["id"] is null)
        {
            var key = crypto.CreateKey(); plan["id"] = key.Id; plan["publicKeyXml"] = key.PublicKeyXml;
            plan["resultPath"] = Path.Combine(Path.GetDirectoryName(path)!, "host-conversion-" + key.Id + ".result.json");
            await tokens.WriteAsync("companion-conversion-" + key.Id, key.PrivateKey, ct);
            files.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!files.WriteFileIfAbsent(path, StateJson.Bytes(plan))) { await tokens.DeleteAsync("companion-conversion-" + key.Id, ct); throw new IpcFailure(409, "conversionConflict", "Another host conversion is already pending."); }
        }
        else files.WriteFileAtomic(path, StateJson.Bytes(plan));
        var resultPath = StateJson.String(plan["resultPath"]);
        if (files.FileExists(resultPath)) files.DeleteFile(resultPath);
        state.PublishSnapshot(entry.Name);
        Notice("Installing Construct host and adopting your VM…");
        await launcher.LaunchElevatedAsync(HostConversionLaunch.Build(plan), ct);
        for (var waited = 0; waited < 3600; waited += 2)
        {
            var result = ReadResult(plan);
            if (result is not null) { state.PublishSnapshot(entry.Name); await Complete(plan, result, ct); return; }
            await clock.DelayAsync(TimeSpan.FromSeconds(2), ct);
        }
        Notice("Host setup is pending. Its PowerShell window shows installation progress. Review host setup to retry or finish.");
    }
    private JsonObject? ReadResult(JsonObject plan) => StateJson.Text(plan["resultPath"]) is {} path ? StateJson.ReadObject(files, path) : null;
    private async Task Complete(JsonObject plan, JsonObject result, CancellationToken ct)
    {
        if (StateJson.Boolean(result["ok"]) != true) throw new IpcFailure(409, "conversionFailed", StateJson.Text(result["error"]) ?? "Host setup stopped. Retry from Settings.");
        HostConversion.ConvertedRegistry(instances.Registry, plan, result);
        var keyName = "companion-conversion-" + StateJson.String(plan["id"]);
        var key = await tokens.ReadAsync(keyName, ct) ?? throw new IpcFailure(409, "conversionKeyMissing", "The conversion's client key is unavailable. Reopen setup in the original Windows user profile.");
        var token = crypto.Decrypt(key, StateJson.String(result["encryptedToken"]));
        var url = StateJson.String(result["url"]); var fingerprint = StateJson.String(result["fingerprint"]);
        var pin = HostIdentity.ReadPin(files, url);
        if (pin.Length > 0 && !HostIdentity.FingerprintsMatch(pin, fingerprint)) throw new IpcFailure(409, "pinMismatch", "The host certificate differs from this PC's saved pin.");
        async Task<JsonObject?> Read(string route)
        {
            var response = await api.SendAsync(new("GET", new Uri(url + "/api/v1" + route), actual => HostIdentity.FingerprintsMatch(actual, fingerprint), Authentication: RemoteAuthentication.Token, Token: token), ct);
            return response.StatusCode == 200 && response.Body is {} body ? JsonNode.Parse(body.GetRawText()) as JsonObject : null;
        }
        var me = await Read("/whoami");
        if (!string.Equals(StateJson.Text(me?["name"]), StateJson.Text(result["owner"]), StringComparison.OrdinalIgnoreCase) || !string.Equals(StateJson.Text(me?["role"]), "admin", StringComparison.OrdinalIgnoreCase)) throw new IpcFailure(403, "administratorMismatch", "The host did not verify the expected administrator.");
        if (HostIdentity.MapVmState(StateJson.Text((await Read("/vms/" + HostIdentity.Encode(StateJson.String(plan["name"])) + "/state"))?["state"])) != "running") throw new IpcFailure(409, "adoptedVmOffline", "The adopted VM is not reporting running; client settings have been preserved.");
        await hosts.AddAsync(new AddRemoteHost(url, token.Reveal(), fingerprint), ct);
        HostConversion.ConvertedRegistry(instances.Registry, plan, result).Save(files);
        files.DeleteFile(HostConversion.PendingPath(instances.Host.LocalAppData)); files.DeleteFile(StateJson.String(plan["resultPath"])); await tokens.DeleteAsync(keyName, ct);
        events.Companion(new { type = "instances" });
        Notice($"This PC is now a Construct host: {url}. {StateJson.String(plan["name"])} is adopted and you are its host administrator.");
    }
}
