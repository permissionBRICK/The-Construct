using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Construct.Companion.Core.State;

public static class InstanceWorkflowPlans
{
    public static JsonObject Register(InstanceRegistry registry, string name, string host)
    {
        name = name.Trim();
        JsonObject Refuse(string reason) => new() { ["ok"] = false, ["reason"] = reason };
        if (!Instances.IsValidName(name)) return Refuse("Not a usable instance name: " + Instances.NameRule);
        if (registry.ByName.ContainsKey(name)) return Refuse($"This PC already has an instance named \"{name}\".");
        var canonical = Instances.DeriveDefaults(name); var endpoint = Regex.Replace(host.Trim().ToLowerInvariant(), "\\.$", "");
        if (endpoint != StateJson.Text(canonical["vmHost"]) && endpoint != StateJson.Text(canonical["hostAlias"]))
            return Refuse($"This window is attached to \"{host}\", but a local Hyper-V instance named \"{name}\" answers at \"{StateJson.Text(canonical["vmHost"])}\" (or \"{StateJson.Text(canonical["hostAlias"])}\"). The registry cannot describe that machine as a \"hyperv-local\" instance — a VM on another host is added through \"Add remote host\" instead.");
        return new() { ["ok"] = true, ["name"] = name, ["entry"] = new JsonObject { ["backend"] = "hyperv-local", ["vmName"] = canonical["vmName"]!.DeepClone(),
            ["sshHost"] = canonical["vmHost"]!.DeepClone(), ["sshPort"] = canonical["sshPort"]!.DeepClone(), ["hostAlias"] = canonical["hostAlias"]!.DeepClone(), ["keyName"] = canonical["keyName"]!.DeepClone() } };
    }
    public static JsonObject Remove(InstanceRegistry registry, string name, string confirmation = "")
    {
        name = name.Trim(); registry.ByName.TryGetValue(name, out var instance);
        string S(string key) => StateJson.Text(instance?[key]) ?? "";
        var plan = new JsonObject { ["ok"] = false, ["refusal"] = "", ["name"] = name.Length == 0 ? null : name, ["backend"] = S("backend"), ["deletesVm"] = false,
            ["requiresTypedConfirmation"] = false, ["confirmationOk"] = true, ["removes"] = new JsonArray(), ["keeps"] = new JsonArray() };
        if (name.Length == 0) { plan["refusal"] = "No instance was named."; return plan; }
        if (instance is null) { plan["refusal"] = $"\"{name}\" is not an instance on this PC."; return plan; }
        if (registry.ByName.Count <= 1) { plan["refusal"] = $"\"{name}\" is the only instance on this PC. Removing it would leave Construct describing a VM whose client state had just been deleted. Add another instance first, or uninstall Construct."; return plan; }
        var remote = Instances.IsRemoteBackend(S("backend")); var service = StateJson.Text(instance["service"]?["url"]) ?? "";
        var removes = new JsonArray($"the ~/.ssh/config Host block and known_hosts entries for \"{S("hostAlias")}\"", $"the private key ~/.ssh/{S("keyName")}",
            $"\"{S("hostAlias")}\" from VS Code's remote.SSH.remotePlatform", $"the OpenCode server entry for \"{name}\"", "this VM's T3 Code certificate authority (file and Root store)",
            $"the per-instance state file instances\\{name}.json", $"\"{name}\" from instances.json");
        if (remote) removes.Insert(0, JsonValue.Create($"the VM \"{name}\" on {(service.Length == 0 ? "its host service" : service)} — including its disk"));
        var keeps = new JsonArray();
        if (!remote) keeps.Add("The Hyper-V VM itself is NOT deleted; Reinstall and Redownload keep working and write the client state again.");
        keeps.Add("The shared config store and this VM's config-sync branch are kept: they hold agent configuration, not client state.");
        var confirmed = !remote || confirmation.Trim() == name;
        plan["ok"] = confirmed; plan["removes"] = removes; plan["keeps"] = keeps; plan["deletesVm"] = remote; plan["requiresTypedConfirmation"] = remote; plan["confirmationOk"] = confirmed;
        if (!confirmed) plan["refusal"] = $"Removing \"{name}\" DELETES the VM on {(service.Length == 0 ? "its host" : service)}, including its disk. Type the instance name exactly (\"{name}\") to confirm.";
        return plan;
    }
    public static bool IsGitUrl(string url) => Regex.IsMatch(url.Trim(), @"\A(?:(?:https?|ssh|git)://[^\s]+|[^@\s]+@[^:\s]+:[^\s]+)\z", RegexOptions.IgnoreCase);
    public static string RepoName(string url) => Regex.Replace(Regex.Split(Regex.Replace(url.Trim(), "[?#].*$", "").TrimEnd('/', '\\'), @"[/\\:]")[^1], "\\.git$", "", RegexOptions.IgnoreCase).Trim();
    public static string CloneScript(string url, string name) => GuestScripts.Render("project-clone", new Dictionary<string,string> {
        ["root"] = "/root/repos", ["url"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url.Trim())), ["dest"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(name)) });
}
