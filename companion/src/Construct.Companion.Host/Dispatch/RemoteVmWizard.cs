using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using HostRecord = Construct.Companion.Core.Ipc.RemoteHost;
namespace Construct.Companion.Host.Dispatch;

// Port of runNewRemoteVm. Auto-Install owns API creation, job/reachability waits,
// registry/VM-spec persistence, credential handling and the Provision-AgentVM call.
public sealed class RemoteVmWizard(CompanionInstances instances, IStateFileSystem files, ITokenStore tokens, IRemoteApi api,
    IPrompts prompts, ILauncher launcher, IpcSettings settings, IpcEvents events)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    public async Task RunAsync(HostRecord host, CancellationToken ct)
    {
        await serial.WaitAsync(ct);
        try { await CreateAsync(host, ct); }
        finally { serial.Release(); }
    }
    private void Notice(string text) => events.Companion(new { type = "notification", text });
    private async Task CreateAsync(HostRecord host, CancellationToken ct)
    {
        var name = (await prompts.InputAsync(new("Name the new VM", "Lowercase letters, digits and hyphens — it becomes the SSH alias, the key file name and the config-sync branch", Placeholder: "work-vm"), ct))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!Instances.IsValidName(name)) { Notice(Instances.NameRule); return; }
        if (instances.Registry.ByName.ContainsKey(name)) { Notice($"This PC already has a Construct instance named \"{name}\"."); return; }
        var client = new RemoteHostClient(api, files, tokens, host.Url, host.Auth == "token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate);
        JsonNode? defaults;
        try { defaults = await client.VmDefaultsAsync(ct); }
        catch (RemoteApiException error) { Notice("Cannot determine the CPU allowance on this host. Update the host service and retry. " + await client.RedactDiagnosticAsync(error.Message, ct)); return; }
        var recommended = StateJson.Number(defaults?["recommendedCpus"]); var maximum = StateJson.Number(defaults?["maximumCpus"]);
        if (recommended is null or < 1 || recommended != Math.Truncate(recommended.Value) || maximum is null or > int.MaxValue || maximum != Math.Truncate(maximum.Value) || maximum < recommended)
        { Notice("No CPU allowance is available for another VM on this host."); return; }
        async Task<int?> Number(string title, string prompt, int initial, int min, int max)
        {
            var raw = await prompts.InputAsync(new(title, prompt, initial.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct);
            if (raw is null) return null;
            var value = StateJson.CoerceNumber(JsonValue.Create(raw.Trim()));
            if (double.IsFinite(value) && value == Math.Truncate(value) && value >= min && value <= max) return (int)value;
            Notice($"A whole number between {min} and {max}."); return null;
        }
        var cpu = await Number("vCPUs", "Defaults to the available user allowance, capped by the host's CPU count and limits.", (int)recommended.Value, 1, (int)maximum.Value); if (cpu is null) return;
        var ram = await Number("Memory (GB)", "How much RAM, in GB?", 8, 1, 1024); if (ram is null) return;
        var disk = await Number("Disk (GB)", "How large should the virtual disk be, in GB?", 50, 8, 8192); if (disk is null) return;
        var directory = instances.Host.ResolveScriptsDirectory(null, settings.Read().ScriptsDir)
            ?? instances.Registry.List().Select(i => instances.Host.ResolveScriptsDirectory(StateJson.Text(i["scriptsDir"]), settings.Read().ScriptsDir)).FirstOrDefault(d => d is not null);
        if (directory is null) { Notice("Couldn't find the Construct scripts on this PC. Set construct.scriptsDir and try again."); return; }
        var script = Path.Combine(directory, "Auto-Install.ps1"); var code = files.ReadFile(script) is {} bytes ? System.Text.Encoding.UTF8.GetString(bytes) : "";
        var missing = new[] { "Backend", "ServiceUrl", "InstanceName" }.Where(p => !LifecycleBuilder.ScriptSupportsParameter(code, p)).ToArray();
        if (missing.Length > 0) { Notice("This PC's Construct scripts are too old to create a VM on a remote host (Auto-Install.ps1 doesn't accept " + string.Join(", ", missing.Select(p => "-" + p)) + "). Update The Construct first."); return; }
        IReadOnlyList<string>? projects = null;
        if (LifecycleBuilder.ScriptSupportsParameter(code, "Projects"))
        {
            var profiles = instances.Host.ListProjectProfiles(instances.Host.ConfigDirectory ?? directory);
            var picked = await prompts.PickAsync(new("Construct projects", profiles.Select(p => new PickItem(p, p, Picked: p == "default")).ToArray(), true), ct);
            if (picked is null) return;
            projects = profiles.Where(picked.Contains).ToArray();
        }
        var plan = RemoteVmLaunch.ArgSpec(host.Url, host.Auth, name, cpu.Value, ram.Value, disk.Value, LifecycleBuilder.ScriptSupportsParameter(code, "VmCpuCount"), projects);
        await launcher.StartDetachedAsync(PowerShellLaunch.BuildHostLaunch(script, RemoteVmLaunch.Arguments(plan), elevate: false, keepOpen: settings.Read().Debug, argSpec: plan).Invocation(directory), ct);
    }
}
