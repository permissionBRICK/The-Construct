using System.Text;
using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.Desktop;

public sealed record SelfTestCheck(string Name, string Status, bool Required = true);
public sealed record SelfTestReport(bool Ok, IReadOnlyList<SelfTestCheck> Checks)
{
    public int ExitCode => Ok ? 0 : 1;
    public static SelfTestReport Create(IReadOnlyList<SelfTestCheck> checks) => new(
        checks.All(c => !c.Required || c.Status is "passed" or "none" or "muted"), checks);
}

// Headless diagnostics for --selftest: never starts tunnels, never writes acks.
public sealed class SelfTest(IStateFileSystem files, ISelfTestPlatform platform, IAudioCapture audio, IToastRaiser toasts)
{
    public async Task<SelfTestReport> RunAsync(string? selectedInstance = null, CancellationToken cancellationToken = default)
    {
        var checks = new List<SelfTestCheck>(); var host = new HostState(files);
        async Task Check(string name, bool required, Func<Task<string>> run)
        {
            try { checks.Add(new(name, await run(), required)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { checks.Add(new(name, "failed", required)); }
        }
        var local = host.LocalAppData;
        checks.Add(new("paths", local is null || string.IsNullOrEmpty(files.GetRoot(FileSystemRoot.InstallDirectory)) ? "failed" : "passed"));
        InstanceRegistry? registry = null;
        await Check("registry", true, () => { registry = InstanceRegistry.LoadUsable(files); return Task.FromResult(registry.Problems.Count == 0 ? "passed" : "failed"); });
        var definitions = registry?.List() ?? [];
        if (selectedInstance is not null)
        {
            checks.Add(new("selectedInstance", definitions.Any(i => StateJson.Text(i["name"]) == selectedInstance) ? "passed" : "failed"));
            definitions = definitions.Where(i => StateJson.Text(i["name"]) == selectedInstance).ToArray();
        }
        await Check("stateFiles", true, () =>
        {
            var paths = new List<string>();
            if (local is not null) paths.Add(Path.Combine(local, "The-Construct", "companion", "settings.json"));
            foreach (var instance in definitions)
            {
                var scripts = host.ResolveScriptsDirectory(StateJson.Text(instance["scriptsDir"]));
                if (scripts is not null) paths.Add(Path.Combine(scripts, HostState.SettingsFile));
                var state = new InstanceStateStore(files, StateJson.Text(instance["name"]), scripts).StatePath;
                if (state is not null) paths.Add(state);
            }
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                if (files.ReadFile(path) is {} content)
                { using var document = JsonDocument.Parse(Encoding.UTF8.GetString(content).TrimStart('\ufeff')); if (document.RootElement.ValueKind != JsonValueKind.Object) return Task.FromResult("failed"); }
            return Task.FromResult("passed");
        });
        await Check("ipcHealth", true, async () => await platform.IpcHealthAsync(cancellationToken) ? "passed" : "failed");
        foreach (var instance in definitions)
        {
            var name = StateJson.Text(instance["name"])!;
            await Check("ssh:" + name, false, async () => await platform.SshProbeAsync(instance, cancellationToken) ? "passed" : "unreachable");
            await Check("hypervisor:" + name, false, async () => (await platform.HypervisorAsync(instance, cancellationToken)).ToString().ToLowerInvariant());
        }
        if (definitions.Count == 0) checks.Add(new("instances", "none", false));
        await Check("audioDevices", true, async () => { var devices = await audio.EnumerateDevicesAsync(cancellationToken); return devices.Count == 0 ? "none" : "passed"; });
        await Check("webView2", true, async () => string.IsNullOrEmpty(await platform.WebViewVersionAsync(cancellationToken)) ? "failed" : "passed");
        var installed = files.GetRoot(FileSystemRoot.InstallDirectory) is {} install && files.FileExists(Path.Combine(install, "install.json"));
        await Check("toastRegistration", installed, async () => (await toasts.GetAvailabilityAsync(cancellationToken)) switch
        { ToastAvailability.Available => "passed", ToastAvailability.Muted => "muted", ToastAvailability.Unregistered => installed ? "failed" : "notInstalled", _ => "failed" });
        return SelfTestReport.Create(checks);
    }
}
