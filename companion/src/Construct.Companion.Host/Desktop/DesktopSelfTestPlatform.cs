using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Host.Desktop;

// --selftest checks against the real OS: the app supplies the WebView2 lookup, the remote state
// query and the health probe of its own (non-publishing) IPC listener.
public sealed class DesktopSelfTestPlatform(IStateFileSystem files, IProcessRunner runner, IHypervisorState hypervisor,
    Func<string?> webViewVersion, Func<CancellationToken, Task<bool>> healthProbe,
    Func<JsonObject, CancellationToken, Task<HypervisorState>>? remoteState = null) : ISelfTestPlatform
{
    public Task<bool> IpcHealthAsync(CancellationToken cancellationToken) => healthProbe(cancellationToken);
    public Task<string?> WebViewVersionAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(webViewVersion()); }
    public async Task<bool> SshProbeAsync(JsonObject instance, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(ProbeInvocation(instance, HostProcesses.SshExecutable(files)), cancellationToken);
        return result.Code == 0 && !string.IsNullOrWhiteSpace(result.Stdout);
    }
    private ProcessInvocation ProbeInvocation(JsonObject instance, string ssh)
    {
        var configuration = new SshConfiguration(StateJson.String(instance["vmHost"]), StateJson.String(instance["hostAlias"]),
            KeyName: StateJson.String(instance["keyName"]), SshPort: Instances.CoercePort(instance["sshPort"]) ?? 22, ConnectTimeout: 8);
        var path = Path.Combine(files.GetRoot(FileSystemRoot.UserProfile) ?? "", ".ssh", configuration.KeyName);
        var args = SshArgs.Build(configuration, SshArgs.WrapScriptCommand(GuestScripts.Render("probe")), files.FileExists(path) ? path : null);
        // A diagnostic must not add new host keys to the user's known_hosts file.
        args = args.Select(a => a == "StrictHostKeyChecking=accept-new" ? "StrictHostKeyChecking=yes" : a).ToArray();
        return new(ssh, args, Timeout: TimeSpan.FromSeconds(15));
    }
    public async Task<HypervisorState> HypervisorAsync(JsonObject instance, CancellationToken cancellationToken)
    {
        if (StateJson.Text(instance["backend"]) == "hyperv-remote") return remoteState is null ? HypervisorState.Unknown : await remoteState(instance, cancellationToken);
        var state = await VmPower.QueryLocalAsync(hypervisor, runner, StateJson.String(instance["vmName"]), cancellationToken);
        return Enum.TryParse<HypervisorState>(state, true, out var result) ? result : HypervisorState.Unknown;
    }
}
