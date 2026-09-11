using System.Text.Json.Nodes;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Lifecycle;

public static class HostConversion
{
    public static string PendingPath(string? localAppData) => string.IsNullOrEmpty(localAppData) ? throw new InvalidOperationException("LOCALAPPDATA is unavailable on this PC.") : Path.Combine(localAppData, "The-Construct", "host-conversion.json");
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
