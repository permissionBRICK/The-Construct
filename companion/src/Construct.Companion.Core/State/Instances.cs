using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Construct.Companion.Core.State;

public static class Instances
{
    public const string DefaultName = "agent-vm";
    public const string NameRule = "1-63 lowercase letters, digits or hyphens, starting and ending with a letter or digit; names starting with \"construct-\" are reserved.";
    private static bool Match(string? s, string pattern) => s is not null && Regex.IsMatch(s, pattern, RegexOptions.ECMAScript);
    public static bool IsValidName(string? name) => Match(name, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$") && !name!.StartsWith("construct-", StringComparison.OrdinalIgnoreCase);
    public static bool IsLocalBackend(string? backend) => StateJson.Trim(backend ?? "").ToLowerInvariant() is "" or "hyperv-local";
    public static bool IsRemoteBackend(string? backend) => StateJson.Trim(backend ?? "").ToLowerInvariant() == "hyperv-remote";
    public static bool IsHostEndpoint(string? s)
    {
        if (Match(s, @"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")) return true;
        if (!Match(s, "^[0-9A-Fa-f:.]{2,45}$") || !s!.Contains(':')) return false;
        if (s.Contains('.') && !Match(s[(s.LastIndexOf(':') + 1)..], @"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}$")) return false;
        return IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
    }
    public static bool IsSafeToken(string? s) => Match(s, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$") && !s!.Contains("..", StringComparison.Ordinal);
    private static bool DeviceName(string s) => Match(s.Split('.')[0].ToLowerInvariant(), "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$");
    public static bool IsKeyFileName(string? s) => Match(s, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$") && !s!.Contains("..", StringComparison.Ordinal) && !s.EndsWith('.') && !DeviceName(s);
    public static bool IsValidConfigBranch(string? s) => Match(s, "^[A-Za-z0-9][A-Za-z0-9._-]*$") && !s!.Contains("..", StringComparison.Ordinal) && !s.EndsWith('.') && !s.EndsWith(".lock", StringComparison.Ordinal) && !DeviceName(s) && s.ToLowerInvariant() is not ("main" or "master" or "head" or "fetch_head" or "orig_head" or "merge_head" or "cherry_pick_head" or "revert_head" or "bisect_head" or "rebase_head" or "auto_merge" or "stash") && (s.ToLowerInvariant() != "vm" || s == "vm");
    public static int? CoercePort(JsonNode? node)
    {
        var number = StateJson.Number(node);
        if (number is null && StateJson.Text(node) is {} s && Match(StateJson.Trim(s), @"^\d{1,5}$")) number = double.Parse(StateJson.Trim(s), CultureInfo.InvariantCulture);
        return number is > 0 and <= 65535 && number == Math.Truncate(number.Value) ? (int)number.Value : null;
    }
    public static JsonObject DeriveDefaults(string name, JsonObject? raw = null)
    {
        raw ??= []; var def = name == DefaultName;
        var backend = raw["backend"] is null ? JsonValue.Create("hyperv-local") : StateJson.Nonempty(raw["backend"]) is {} b ? JsonValue.Create(b) : raw["backend"]!.DeepClone();
        var service = raw["service"] as JsonObject;
        return new JsonObject
        {
            ["name"] = name, ["backend"] = backend,
            ["vmName"] = StateJson.Nonempty(raw["vmName"]) ?? (def ? "Agent-VM" : name),
            ["vmHost"] = StateJson.Nonempty(raw["sshHost"]) ?? StateJson.Nonempty(raw["vmHost"]) ?? name + ".mshome.net",
            ["sshPort"] = CoercePort(raw["sshPort"]) ?? 22,
            ["hostAlias"] = StateJson.Nonempty(raw["hostAlias"]) ?? name,
            ["keyName"] = StateJson.Nonempty(raw["keyName"]) ?? (def ? "agent_vm_ed25519" : "construct_" + name + "_ed25519"),
            ["configBranch"] = StateJson.Nonempty(raw["configBranch"]) ?? (def ? "vm" : "vm-" + name),
            ["scriptsDir"] = StateJson.Nonempty(raw["scriptsDir"]),
            ["service"] = service is not null && StateJson.Nonempty(service["url"]) is {} url ? new JsonObject { ["url"] = url, ["auth"] = StateJson.Text(service["auth"]) == "token" ? "token" : "negotiate" } : null,
            ["owner"] = StateJson.Nonempty(raw["owner"]),
            ["publicHost"] = IsLocalBackend(StateJson.String(backend)) ? null : StateJson.Nonempty(raw["publicHost"])
        };
    }
    public static bool IsDefaultInstance(JsonObject? instance) => instance is null || new[] { "name", "backend", "vmName", "vmHost", "sshPort", "hostAlias", "keyName", "configBranch" }.All(k => JsonNode.DeepEquals(instance[k], DeriveDefaults(DefaultName)[k]));
    public static string[] BackendProblems(JsonNode? raw)
    {
        if (raw is null) return [];
        var v = StateJson.Nonempty(raw);
        if (v is null) return [$"\"backend\" {StateJson.Quote(raw)} is not a usable backend id (omit it for \"hyperv-local\", or name one of: hyperv-local, hyperv-remote)"];
        if (v is "hyperv-local" or "hyperv-remote") return [];
        if (v.ToLowerInvariant() is "hyperv-local" or "hyperv-remote") return [$"\"backend\" {StateJson.Quote(JsonValue.Create(v))} is not spelled \"{v.ToLowerInvariant()}\" (the backend id is case-sensitive, but the driver lookup is not — a value the two read differently must not drive a VM)"];
        return [];
    }
    public static string[] IdentityProblems(JsonObject instance, JsonObject? raw = null)
    {
        var result = new List<string>();
        string Q(string key) => StateJson.Quote(instance[key]);
        string? S(string key) => StateJson.Text(instance[key]);
        foreach (var key in new[] { "sshHost", "vmHost" })
            if (raw is not null && StateJson.Nonempty(raw[key]) is {} value && !IsHostEndpoint(value)) result.Add($"\"{key}\" {StateJson.Quote(JsonValue.Create(value))} is not a host name or IP address");
        if (!IsValidName(S("name"))) result.Add($"\"name\" {Q("name")} is not a usable instance name ({NameRule})");
        if (!Match(S("vmName"), "^[A-Za-z0-9][A-Za-z0-9-]{0,62}$")) result.Add($"\"vmName\" {Q("vmName")} is not a usable VM/host name (letters, digits and hyphens, starting alphanumeric, max 63)");
        if (!IsHostEndpoint(S("vmHost"))) result.Add($"\"sshHost\" {Q("vmHost")} is not a host name or IP address");
        if (!IsSafeToken(S("hostAlias"))) result.Add($"\"hostAlias\" {Q("hostAlias")} is not a usable ssh alias (letters, digits, '.', '_' and '-', max 64)");
        if (!IsKeyFileName(S("keyName"))) result.Add($"\"keyName\" {Q("keyName")} is not a usable key file name (letters, digits, '.', '_' and '-', max 128; no trailing dot and not a reserved Windows device name)");
        if (!IsValidConfigBranch(S("configBranch"))) result.Add($"\"configBranch\" {Q("configBranch")} is not a usable config-sync branch name");
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }
    public static string[] LocalIdentityProblems(JsonObject instance)
    {
        if (!IsLocalBackend(instance["backend"] is null ? null : StateJson.String(instance["backend"]))) return [];
        var canonical = DeriveDefaults(StateJson.String(instance["name"])); var result = new List<string>();
        const string why = " (a \"hyperv-local\" instance's identity is derived from its name — Auto-Install.ps1 rebuilds it that way, so anything else targets another VM)";
        foreach (var key in new[] { "vmName", "vmHost", "hostAlias", "keyName", "sshPort" })
        {
            var equal = key == "vmName" ? string.Equals(StateJson.String(instance[key]), StateJson.String(canonical[key]), StringComparison.OrdinalIgnoreCase) : JsonNode.DeepEquals(instance[key], canonical[key]);
            if (!equal) result.Add($"\"{(key == "vmHost" ? "sshHost" : key)}\" {StateJson.Quote(instance[key])} must be {StateJson.Quote(canonical[key])} for instance {StateJson.Quote(instance["name"])}{why}");
        }
        return result.ToArray();
    }
    public static string[] RemoteIdentityProblems(JsonObject instance, JsonObject? raw)
    {
        if (!IsRemoteBackend(StateJson.Text(instance["backend"]))) return [];
        var result = new List<string>();
        if (raw is null || StateJson.Nonempty(raw["sshHost"]) is null) result.Add("\"sshHost\" is missing (no sshHost) — a \"hyperv-remote\" instance's endpoint is the one its host service allocated, so it cannot be derived from the instance name");
        if (StateJson.String(instance["vmName"]) != StateJson.Text(instance["name"])) result.Add($"\"vmName\" {StateJson.Quote(instance["vmName"])} must be {StateJson.Quote(instance["name"])} for a \"hyperv-remote\" instance (the host service addresses the VM by that name, so the power state, Start and a rebuild would otherwise act on two different VMs)");
        return result.ToArray();
    }
    public static string TargetFingerprint(JsonObject? instance)
    {
        if (instance is null) return "";
        string S(string key) => instance[key] is null ? "" : StateJson.String(instance[key]);
        var service = instance["service"] as JsonObject;
        var port = StateJson.CoerceNumber(instance["sshPort"]); if (double.IsNaN(port)) port = 0;
        string V(string key) => service?[key] is {} value ? StateJson.String(value) : "";
        return StateJson.Stringify(new JsonArray(S("name"), StateJson.Trim(S("backend")).ToLowerInvariant(), S("vmName"), S("vmHost"), port, S("hostAlias"), S("keyName"), S("configBranch"), S("scriptsDir"), V("url"), V("auth"), S("owner")));
    }
}
