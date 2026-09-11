using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Drivers;

public static class VmPower
{
    public const string ShutdownCommand = "systemctl poweroff --no-block";
    public static string BuildStateProbeCommand(string? name) => "try { $vm = Get-VM -Name " + PowerShellLaunch.SingleQuote(string.IsNullOrEmpty(name) ? "Agent-VM" : name) + " -ErrorAction Stop; Write-Output ('VMSTATE=' + $vm.State) } catch { if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { Write-Output 'VMSTATE=absent' } else { Write-Output 'VMSTATE=unknown' } }";
    public static HostLaunch BuildStateProbeLaunch(string? name) => PowerShellLaunch.Probe(BuildStateProbeCommand(name));
    public static string ParseVmState(string? stdout) => RemoteHost.MapVmState(Regex.Match(stdout ?? "", @"VMSTATE=(\S+)", RegexOptions.ECMAScript).Groups[1].Value);
    public static string BuildAutoCheckpointProbeCommand(string? name) => "try { $vm = Get-VM -Name " + PowerShellLaunch.SingleQuote(string.IsNullOrEmpty(name) ? "Agent-VM" : name) + " -ErrorAction Stop; $p = $vm.PSObject.Properties['AutomaticCheckpointsEnabled']; if ($p -and $null -ne $p.Value) { Write-Output ('VMAUTOCHK=' + [bool]$p.Value) } else { Write-Output 'VMAUTOCHK=unsupported' } } catch { if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { Write-Output 'VMAUTOCHK=absent' } else { Write-Output 'VMAUTOCHK=unknown' } }";
    public static HostLaunch BuildAutoCheckpointProbeLaunch(string? name) => PowerShellLaunch.Probe(BuildAutoCheckpointProbeCommand(name));
    public static string ParseAutoCheckpoints(string? stdout) => Regex.Match(stdout ?? "", @"VMAUTOCHK=(\S+)", RegexOptions.ECMAScript).Groups[1].Value.ToLowerInvariant() switch { "true" => "on", "false" => "off", "absent" => "absent", "unsupported" => "unsupported", _ => "unknown" };
    public static string RefineSavedState(string state, string? backend, string? rawState) => state == "off" && Instances.IsRemoteBackend(backend) && StateJson.Trim(rawState ?? "").Equals("saved", StringComparison.OrdinalIgnoreCase) ? "saved" : state;
    public static string? LifecycleRefusal(string? backend, string action)
    {
        if (action is not ("reinstall" or "redownload" or "setCheckpoints" or "setResources")) return null;
        var key = StateJson.Trim(string.IsNullOrEmpty(backend) ? "hyperv-local" : backend).ToLowerInvariant();
        if (key is not ("hyperv-local" or "hyperv-remote")) return $"the \"{backend}\" backend can't be rebuilt or reconfigured from here — the host scripts drive the local Hyper-V. Reprovision and Export config still work.";
        if (action == "setCheckpoints" && key == "hyperv-remote") return "the \"hyperv-remote\" backend has no checkpoints — that setting applies to VMs on this PC's Hyper-V only.";
        if (action == "setResources" && key == "hyperv-remote") return "the \"hyperv-remote\" backend is not resized by the host scripts — a VM on a host service is resized through that service.";
        return null;
    }
    public static string BuildStartCommand(string? name) => "Start-VM -Name " + PowerShellLaunch.SingleQuote(string.IsNullOrEmpty(name) ? "Agent-VM" : name) + "; if ($?) { Write-Host 'Construct VM started.' -ForegroundColor Green } else { Write-Host 'Failed to start the Construct VM.' -ForegroundColor Red; if (-not [Console]::IsInputRedirected) { [void](Read-Host 'Press Enter to close') } }";
    public static HostLaunch BuildElevatedCommandLaunch(string commandText, bool keepOpen = false)
    {
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass" }; if (keepOpen) args.Add("-NoExit"); args.AddRange(["-Command", commandText]);
        var command = PowerShellLaunch.BuildOuterCommand(string.Join(" ", args.Select(PowerShellLaunch.WinQuoteArg)), true);
        return new HostLaunch("cmd.exe", ["/c", "start", "", "powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", PowerShellLaunch.Encode(command)], command);
    }
    public static async Task<string> QueryLocalAsync(IHypervisorState hypervisor, IProcessRunner runner, string vmName, CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await hypervisor.QueryAsync(vmName, cancellationToken);
            if (state != HypervisorState.Unknown) return RemoteHost.MapVmState(state.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* denied CIM uses the established Get-VM fallback */ }
        try { var result = await runner.RunAsync(BuildStateProbeLaunch(vmName).Invocation() with { Timeout = TimeSpan.FromSeconds(15) }, cancellationToken); return ParseVmState(result.Stdout); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return "unknown"; }
    }
    public static async Task<JsonArray> QueryChildrenAsync(RemoteHostClient client, string vmName, CancellationToken cancellationToken = default)
    {
        var children = client.ChildrenAsync(vmName, cancellationToken);
        var shared = client.SharedVmsAsync(cancellationToken);
        await Task.WhenAll(children, shared);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new JsonArray((children.Result as JsonArray ?? []).Concat(shared.Result as JsonArray ?? [])
            .OfType<JsonObject>().Where(vm => names.Add(StateJson.String(vm["name"]))).Select(vm => vm.DeepClone()).ToArray());
    }
    public static async Task<string> QueryRemoteAsync(RemoteHostClient client, string vmName, CancellationToken cancellationToken = default)
    {
        try { var result = await client.GetStateAsync(vmName, cancellationToken); return RemoteHost.MapVmState(StateJson.Text((result as JsonObject)?["state"])); }
        catch (RemoteApiException e) { return e.Status == 404 ? "absent" : "unknown"; }
    }
}
