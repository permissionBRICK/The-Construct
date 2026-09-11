using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Lifecycle;

public static class HostConversion
{
    public static bool Eligible(JsonObject? instance, bool connected, bool isWindows) => isWindows && connected && StateJson.Text(instance?["backend"]) == "hyperv-local";
    public static bool ValidHost(string? host) => host is not null && Regex.IsMatch(host, "^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$", RegexOptions.IgnoreCase) && !host.Contains("..", StringComparison.Ordinal);
    public static string PendingPath(string? localAppData) => string.IsNullOrEmpty(localAppData) ? throw new InvalidOperationException("LOCALAPPDATA is unavailable on this PC.") : Path.Combine(localAppData, "The-Construct", "host-conversion.json");
    public static string LaunchScript(JsonObject plan)
    {
        var script = Path.Combine(StateJson.String(plan["scriptsDir"]), "service", "host", "ConvertTo-ConstructHost.ps1");
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(StateJson.Stringify(plan)));
        var inner = PowerShellLaunch.Encode($"& {PowerShellLaunch.SingleQuote(script)} -PlanB64 '{base64}'; exit $LASTEXITCODE");
        return $"$ErrorActionPreference='Stop'; try {{ $p=Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {inner}'; exit $p.ExitCode }} catch {{ Write-Error 'Host setup could not start. The UAC request may have been cancelled.'; exit 1 }}";
    }
    public static InstanceRegistry ConvertedRegistry(InstanceRegistry registry, JsonObject plan, JsonObject result)
    {
        var name = StateJson.String(plan["name"]); var url = StateJson.Text(result["url"]);
        if (StateJson.Boolean(result["ok"]) != true || !JsonNode.DeepEquals(result["id"], plan["id"]) || StateJson.Text(result["name"]) != name || !JsonNode.DeepEquals(result["owner"], plan["adminUser"]) || url != $"https://{StateJson.String(plan["publicHost"])}:7462" || StateJson.Number(result["sshPort"]) is not {} port || port < 1 || port > 65535 || port != Math.Truncate(port)) throw new InvalidOperationException("The conversion result does not match this VM's setup request.");
        registry.ByName.TryGetValue(name, out var current);
        var already = current is not null && StateJson.Text(current["backend"]) == "hyperv-remote" && RemoteHost.SameServiceUrl(StateJson.Text((current["service"] as JsonObject)?["url"]), url);
        if (current is null || (!already && Instances.TargetFingerprint(current) != StateJson.Text(plan["fingerprint"]))) throw new InvalidOperationException("The instance changed during setup. Host adoption succeeded; client conversion needs its original instance restored.");
        return registry.Update(name, new JsonObject { ["backend"] = "hyperv-remote", ["vmName"] = name, ["sshHost"] = current["vmHost"]?.DeepClone(), ["sshPort"] = current["sshPort"]?.DeepClone(), ["owner"] = result["owner"]?.DeepClone(), ["publicHost"] = result["publicHost"]?.DeepClone(), ["service"] = new JsonObject { ["url"] = url, ["auth"] = "token" } });
    }
    public static JsonObject? PendingStatus(string name, JsonObject? plan, JsonObject? result)
    {
        if (plan is null || StateJson.Text(plan["name"]) != name) return null;
        var ready = StateJson.Boolean(result?["ok"]) == true;
        return new JsonObject { ["ready"] = ready, ["message"] = result is null ? "Host setup is pending. Its PowerShell window shows installation progress." : ready ? "Host installation finished. Finish conversion to verify access and connect this window." : StateJson.Nonempty(result["error"]) ?? "Host setup stopped. Review and retry setup." };
    }
}
