using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;

namespace Construct.Companion.Core.Drivers;

public static class LocalConsole
{
    public static ProcessInvocation BuildHandoffLaunch(string scriptsDir, string instance, string vmName)
    {
        var command = "$ErrorActionPreference='Stop'; $u=New-Object Text.UTF8Encoding($false); [Console]::OutputEncoding=$u; [Console]::InputEncoding=$u; " +
            ". " + PowerShellLaunch.SingleQuote(Path.Combine(scriptsDir, "lib", "AgentVm.Remote.ps1")) + "; . " + PowerShellLaunch.SingleQuote(Path.Combine(scriptsDir, "lib", "AgentVm.Console.ps1")) + "; " +
            "$p=[Console]::In.ReadToEnd() | ConvertFrom-Json; Invoke-ConstructConsoleHandoff -InstanceName $p.instanceName -VmName $p.vmName";
        return PowerShellLaunch.Probe(command).Invocation() with { StandardInput = new Secret(new JsonObject { ["instanceName"] = instance, ["vmName"] = vmName }.ToJsonString()), Timeout = TimeSpan.FromSeconds(30), CreateNoWindow = true };
    }
    public static ProcessInvocation BuildSetupLaunch(string scriptsDir, string instance, string vmName, bool reset = false) =>
        PowerShellLaunch.BuildHostLaunch(Path.Combine(scriptsDir, "Set-AgentVmConsoleAccess.ps1"), ["-FromPanel", "-InstanceName", instance, "-VmName", vmName, .. (reset ? new[] { "-Reset" } : Array.Empty<string>())], elevate: true).Invocation();
}
