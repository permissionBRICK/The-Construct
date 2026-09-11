using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Lifecycle;

public static class LifecycleBuilder
{
    public static string? ScriptForAction(string action) => action switch { "reprovision" or "exportConfig" => "Provision-AgentVM.ps1", "reinstall" or "redownload" => "Auto-Install.ps1", "setCheckpoints" => "Set-AgentVmCheckpoints.ps1", "setResources" => "Set-AgentVmResources.ps1", _ => null };
    private static string Label(string action) => action switch { "reprovision" => "Reprovision", "exportConfig" => "Export config", "reinstall" => "Reinstall", "redownload" => "Redownload", "setCheckpoints" => "Automatic checkpoints", "setResources" => "Apply VM resources", "removeInstance" => "Remove instance", _ => action };
    public static string[] ParamsForAction(string action, JsonObject? instance, string[]? declared = null)
    {
        if (Instances.IsRemoteBackend(StateJson.Text(instance?["backend"])) && action is "reinstall" or "redownload") return ["Backend", "ServiceUrl", "InstanceName", "ConfigBranch"];
        if (declared?.Contains("InstanceName", StringComparer.Ordinal) == true) return action switch { "reprovision" or "reinstall" or "redownload" => ["InstanceName", "ConfigBranch"], "exportConfig" or "setCheckpoints" or "setResources" => ["InstanceName"], _ => [] };
        return action switch { "reprovision" => ["VmHost", "HostAlias", "SshPort", "LocalKeyName", "ConfigBranch"], "exportConfig" => ["VmHost", "HostAlias", "SshPort", "LocalKeyName"], "reinstall" or "redownload" => ["VmName", "ConfigBranch"], "setCheckpoints" or "setResources" => ["VmName"], _ => [] };
    }
    public static string DerivedConfigBranch(string? alias)
    {
        alias = StateJson.Trim(alias ?? "").ToLowerInvariant(); if (alias is "" or "agent-vm") return "vm"; var branch = "vm-" + alias; return Instances.IsValidConfigBranch(branch) ? branch : "vm";
    }
    public static string? ConfigBranchOverride(JsonObject? instance)
    {
        if (instance is null || Instances.IsDefaultInstance(instance)) return null;
        var want = StateJson.Text(instance["configBranch"]); return Instances.IsValidConfigBranch(want) && want != DerivedConfigBranch(StateJson.Text(instance["hostAlias"])) ? want : null;
    }
    public static JsonArray InstanceArgPairs(string action, JsonObject? instance, string[]? declared = null)
    {
        var pairs = new JsonArray(); if (instance is null || Instances.IsDefaultInstance(instance)) return pairs;
        foreach (var p in ParamsForAction(action, instance, declared))
        {
            if (declared is not null && !declared.Contains(p, StringComparer.Ordinal)) continue;
            var v = p switch { "VmHost" => StateJson.Text(instance["vmHost"]), "HostAlias" => StateJson.Text(instance["hostAlias"]), "SshPort" => StateJson.String(instance["sshPort"]), "LocalKeyName" => StateJson.Text(instance["keyName"]), "VmName" => StateJson.Text(instance["vmName"]), "ConfigBranch" => ConfigBranchOverride(instance), "Backend" => StateJson.Text(instance["backend"]), "ServiceUrl" => StateJson.Text((instance["service"] as JsonObject)?["url"]), "InstanceName" => StateJson.Text(instance["name"]), _ => null };
            if (!string.IsNullOrEmpty(v)) pairs.Add(new JsonObject { ["flag"] = "-" + p, ["value"] = v });
        }
        return pairs;
    }
    public static string[] FlattenArgPairs(JsonArray pairs) => pairs.OfType<JsonObject>().SelectMany(p => p.ContainsKey("value") ? new[] { StateJson.String(p["flag"]), StateJson.String(p["value"]) } : [StateJson.String(p["flag"])]).ToArray();
    public static string[] InstanceArgs(string action, JsonObject? instance, string[]? declared = null) => FlattenArgPairs(InstanceArgPairs(action, instance, declared));
    public static JsonObject? CheckInstanceSupport(string action, JsonObject? instance, string[]? declared = null)
    {
        if (instance is null || Instances.IsDefaultInstance(instance)) return null;
        var label = Label(action); var name = StateJson.Text(instance["name"]) ?? "this instance";
        JsonObject Block(string reason) => new() { ["blocked"] = true, ["reason"] = reason };
        if (VmPower.LifecycleRefusal(StateJson.Text(instance["backend"]), action) is {} refusal) return Block($"{label} can't run for instance \"{name}\": {refusal}");
        if (Instances.IsRemoteBackend(StateJson.Text(instance["backend"])) && action is "reinstall" or "redownload" && StateJson.Nonempty((instance["service"] as JsonObject)?["url"]) is null) return Block($"{label} can't run for instance \"{name}\": its registry entry records no host service (service.url), so there is nothing to ask for a new VM. Add the host again, or fix the entry.");
        if (declared is null) return null;
        var wanted = ParamsForAction(action, instance, declared); var missing = wanted.Where(p => p != "ConfigBranch" && !declared.Contains(p, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0) return Block($"{label} can't target instance \"{name}\": this Construct install's {ScriptForAction(action) ?? "host scripts"} doesn't accept {string.Join(", ", missing.Select(p => "-" + p))}, so the action would run against the DEFAULT VM. Update the Construct scripts first.");
        if (wanted.Contains("ConfigBranch", StringComparer.Ordinal) && !declared.Contains("ConfigBranch", StringComparer.Ordinal)) return Block($"{label} can't target instance \"{name}\": its config-sync branch \"{StateJson.Text(instance["configBranch"]) ?? DerivedConfigBranch(StateJson.Text(instance["hostAlias"]))}\" needs -ConfigBranch, which this install's {ScriptForAction(action) ?? "host scripts"} doesn't accept — the VM would be initialised on a different branch than the panel syncs. Update the Construct scripts first.");
        return null;
    }
    public static JsonObject? BuildInvocation(string action, JsonObject? options = null)
    {
        options ??= []; var settings = options["settings"] as JsonObject ?? []; var instance = options["instance"] as JsonObject;
        var declared = options["instanceParams"] is JsonArray array ? array.Select(StateJson.String).ToArray() : null;
        var blocked = CheckInstanceSupport(action, instance, declared); if (blocked is not null) return blocked;
        var pairs = new JsonArray(new JsonObject { ["flag"] = "-FromPanel" }); foreach (var pair in InstanceArgPairs(action, instance, declared)) pairs.Add(pair?.DeepClone());
        void Pair(string flag, JsonNode? value) => pairs.Add(new JsonObject { ["flag"] = flag, ["value"] = value?.DeepClone() });
        void Text(string flag, string key) { if (settings[key] is {} value && StateJson.Trim(StateJson.String(value)).Length > 0) Pair(flag, JsonValue.Create(StateJson.String(value))); }
        void Bool(string flag, string key) { if (StateJson.Boolean(settings[key]) is bool value) Pair(flag, JsonValue.Create(value ? "true" : "false")); }
        void Projects() { if (options["projects"] is JsonArray projects) { var clean = projects.Where(p => p is not null && StateJson.Text(p) != "" && StateJson.Boolean(p) != false).Select(StateJson.String).ToArray(); if (clean.Length > 0) Pair("-Projects", JsonValue.Create(string.Join(",", clean))); } }
        bool Supports(string key) => StateJson.Boolean(options[key]) != false;
        void Patches()
        {
            Bool("-ClaudePartialStreaming", "partialStreaming"); Bool("-MicPassthrough", "mic");
            if (Supports("supportsOpenCodeBackgroundWatcher")) Bool("-OpenCodeBackgroundWatcher", "opencodeBackgroundWatcher");
            Bool("-T3Code", "t3code"); if (Supports("supportsT3CodeChannel")) Text("-T3CodeChannel", "t3codeChannel"); if (Supports("supportsT3CodeLimitResume")) Bool("-T3CodeLimitResume", "t3codeLimitResume");
        }
        JsonObject Done(string script, bool destructive, bool elevate, string label)
        {
            var result = new JsonObject { ["script"] = script, ["args"] = new JsonArray(FlattenArgPairs(pairs).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["destructive"] = destructive, ["elevate"] = elevate, ["label"] = label };
            if (instance is not null && !Instances.IsDefaultInstance(instance)) result["argSpec"] = pairs; return result;
        }
        switch (action)
        {
            case "reprovision":
                Pair("-Action", JsonValue.Create("provision")); Projects(); Text("-GitUserName", "gitName"); Text("-GitEmail", "gitEmail"); Bool("-VsCodeServeWeb", "serveWeb"); Bool("-VsCodeTunnel", "tunnel"); Bool("-SmbShare", "smb"); Patches(); pairs.Add(new JsonObject { ["flag"] = "-NonInteractive" }); return Done("Provision-AgentVM.ps1", false, false, "Reprovision");
            case "exportConfig": Pair("-Action", JsonValue.Create("export")); Pair("-BackupDir", options["backupDir"]); return Done("Provision-AgentVM.ps1", false, false, "Export config");
            case "reinstall": case "redownload":
                Pair("-Action", JsonValue.Create(action)); Pair("-BackupMode", JsonValue.Create(StateJson.Text(options["backupMode"]) is "existing" or "wipe" ? StateJson.Text(options["backupMode"]) : "save")); Projects(); Text("-VmMemoryGB", "ram"); Text("-VmDiskGB", "disk"); if (Supports("supportsVmCpuCount")) Text("-VmCpuCount", "cpu"); if (Supports("supportsCheckpoints")) Bool("-AutomaticCheckpoints", "autoCheckpoints"); if (action == "redownload") Text("-UbuntuRelease", "ubuntu"); Text("-GitUserName", "gitName"); Text("-GitEmail", "gitEmail"); Patches(); return Done("Auto-Install.ps1", true, !Instances.IsRemoteBackend(StateJson.Text(instance?["backend"])), Label(action));
            case "setCheckpoints":
                if (StateJson.Boolean(options["enabled"]) is not bool enabled) return null;
                Pair("-Enabled", JsonValue.Create(enabled ? "true" : "false")); return Done("Set-AgentVmCheckpoints.ps1", false, true, enabled ? "Enable automatic checkpoints" : "Disable automatic checkpoints");
            case "setResources":
                var ram = VmResourcePlan.Number(options["ram"], false); var cpu = VmResourcePlan.Number(options["cpu"], true);
                if (ram is null && cpu is null) return null;
                if (ram is not null) Pair("-VmMemoryGB", JsonValue.Create(StateJson.String(JsonValue.Create(ram))));
                if (cpu is not null) Pair("-VmCpuCount", JsonValue.Create(StateJson.String(JsonValue.Create(cpu))));
                return Done("Set-AgentVmResources.ps1", false, true, "Apply VM resources");
            case "removeInstance":
                if (instance is null || StateJson.Nonempty(instance["name"]) is null) return null;
                Pair("-Action", JsonValue.Create("remove-instance")); Pair("-InstanceName", instance["name"]); if (options["confirmation"] is {} confirmation && StateJson.String(confirmation).Length > 0) Pair("-ConfirmInstanceName", JsonValue.Create(StateJson.String(confirmation))); return Done("Auto-Install.ps1", false, false, "Remove instance");
            default: return null;
        }
    }
    public static bool ScriptSupportsParameter(string source, string parameter)
    {
        var code = Regex.Replace(Regex.Replace(source, @"<#[\s\S]*?#>", ""), @"^[ \t]*#.*$", "", RegexOptions.Multiline);
        return Regex.IsMatch(code, @"\$" + Regex.Escape(parameter) + @"\s*(?:=|,|\)|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }
    public static string[] InstanceParameterSupport(IFileSystem files, string directory, string action, JsonObject? instance)
    {
        var script = ScriptForAction(action); if (script is null) return [];
        var bytes = files.ReadFile(Path.Combine(directory, script)); if (bytes is null) return []; var code = System.Text.Encoding.UTF8.GetString(bytes);
        var named = files.FileExists(Path.Combine(directory, "lib", "AgentVm.InstanceTarget.ps1")) && ScriptSupportsParameter(code, "InstanceName") && !(Instances.IsRemoteBackend(StateJson.Text(instance?["backend"])) && action is "reinstall" or "redownload");
        return ParamsForAction(action, instance, named ? ["InstanceName"] : null).Where(p => ScriptSupportsParameter(code, p)).ToArray();
    }
}

public sealed record ResultPollingPlan(string File, string EnvironmentKey, TimeSpan Interval, TimeSpan Timeout)
{
    public static ResultPollingPlan Create(string tempDirectory, string action, long timestamp) => action switch
    {
        "update" => new(Path.Combine(tempDirectory, $"construct-update-{timestamp}.result"), "CONSTRUCT_UPDATE_RESULT", TimeSpan.FromMilliseconds(1500), TimeSpan.FromMinutes(10)),
        "setResources" => new(Path.Combine(tempDirectory, $"construct-resources-{timestamp}.result"), "CONSTRUCT_RESOURCES_RESULT", TimeSpan.FromMilliseconds(1500), TimeSpan.FromMinutes(20)),
        "setCheckpoints" => new(Path.Combine(tempDirectory, $"construct-checkpoints-{timestamp}.result"), "CONSTRUCT_CHECKPOINT_RESULT", TimeSpan.FromMilliseconds(1500), TimeSpan.FromMinutes(10)),
        _ => throw new ArgumentException("Unknown result-file action")
    };
    public string Evaluate(string? text, TimeSpan elapsed) => StateJson.Trim(text ?? "") switch { "ok" => "ok", "fail" => "fail", _ => elapsed > Timeout ? "timeout" : "pending" };
}
