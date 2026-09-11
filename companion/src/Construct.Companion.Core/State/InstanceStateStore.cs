using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Core.State;

public sealed class InstanceStateStore(IStateFileSystem files, string? name, string? scriptsDirectory)
{
    public static readonly string[] InstallWideKeys = ["installedCommit", "constructRepo", "constructRef", "gitUserName", "gitEmail", "gitCredentialStore"];
    private readonly HostState host = new(files);
    public string Name { get; } = name ?? "agent-vm";
    public string? ScriptsDirectory { get; } = scriptsDirectory;
    public bool IsDefault => StateJson.Trim(Name) is "" or "agent-vm";
    public string? StatePath => !IsDefault && Instances.IsValidName(StateJson.Trim(Name)) && host.LocalAppData is {} root
        ? Path.Combine(root, "The-Construct", "instances", StateJson.Trim(Name) + ".json") : null;
    public JsonObject ReadInstallWide() => host.ReadRawSettings(ScriptsDirectory);
    public JsonObject ReadState()
    {
        if (IsDefault) return ReadInstallWide();
        var raw = StateJson.ReadObject(files, StatePath) ?? [];
        foreach (var key in InstallWideKeys.Concat(["version", "instance"])) raw.Remove(key);
        return raw;
    }
    public JsonObject ReadMerged()
    {
        var state = ReadState(); if (IsDefault) return state;
        var wide = ReadInstallWide(); var result = new JsonObject();
        foreach (var key in InstallWideKeys) if (wide.ContainsKey(key)) result[key] = wide[key]?.DeepClone();
        return StateJson.Merge(result, state);
    }
    public JsonObject SaveState(JsonObject? patch)
    {
        patch ??= [];
        if (IsDefault) { var merged = StateJson.Merge(ReadState(), patch); host.WriteRawSettings(ScriptsDirectory, merged); return merged; }
        RequireStatePath();
        var wide = new JsonObject(); var scoped = new JsonObject();
        foreach (var (key, value) in patch) (InstallWideKeys.Contains(key, StringComparer.Ordinal) ? wide : scoped)[key] = value?.DeepClone();
        if (wide.Count > 0) host.WriteRawSettings(ScriptsDirectory, StateJson.Merge(ReadInstallWide(), wide));
        if (scoped.Count == 0) return ReadState();
        var result = StateJson.Merge(ReadState(), scoped); WriteState(result); return result;
    }
    private string RequireStatePath() => StatePath ?? throw new InvalidOperationException("Not a usable Construct instance name for a state file");
    private void WriteState(JsonObject values)
    {
        var path = RequireStatePath(); var doc = new JsonObject { ["version"] = 1, ["instance"] = StateJson.Trim(Name) };
        foreach (var key in values.Select(p => p.Key).Order(StringComparer.Ordinal)) doc[key] = values[key]?.DeepClone();
        files.CreateDirectory(Path.GetDirectoryName(path)!); files.WriteFileAtomic(path, StateJson.Bytes(doc));
    }
    public JsonObject ReplaceState(JsonObject next)
    {
        if (IsDefault) { host.WriteRawSettings(ScriptsDirectory, next); return (JsonObject)next.DeepClone(); }
        var scoped = (JsonObject)next.DeepClone(); foreach (var key in InstallWideKeys.Concat(["version", "instance"])) scoped.Remove(key);
        WriteState(scoped); return scoped;
    }
    public JsonObject ReadSettings() => SettingsMapping.MapToForm(ReadMerged());
    public JsonObject SaveSettings(JsonObject form) => SaveState(SettingsMapping.MapFromForm(form));
    public JsonArray ReadSelectedProjects() => HostState.CleanSelection(ReadState()["projects"]);
    public bool HasPersistedSelection() => ReadState()["projects"] is JsonArray;
    public JsonObject SaveSelectedProjects(JsonNode? names) => SaveState(new JsonObject { ["projects"] = HostState.CleanSelection(names) });
    public bool? ReadAppliedAutoCheckpoints() => StateJson.Boolean(ReadState()["vmAutoCheckpointsApplied"]);
    public JsonObject SaveAppliedAutoCheckpoints(bool? value)
    {
        if (value.HasValue) return SaveState(new JsonObject { ["vmAutoCheckpointsApplied"] = value.Value });
        var state = ReadState(); state.Remove("vmAutoCheckpointsApplied"); return ReplaceState(state);
    }
    public JsonObject? BackfillFromProbe(JsonObject? probe)
    {
        if (probe is null || StateJson.Boolean(probe["online"]) != true || StateJson.Truthy(probe["probeError"])) return null;
        var current = ReadState(); var patch = new JsonObject();
        var spec = probe["vmSpec"] as JsonObject ?? []; var config = probe["vmConfig"] as JsonObject ?? [];
        foreach (var (key, field) in new[] { ("vmMemoryGB", "ramGb"), ("vmDiskGB", "diskGb"), ("vmCpuCount", "cpus") })
            if (!current.ContainsKey(key) && StateJson.Number(spec[field]) is > 0 and var n && double.IsFinite(n)) patch[key] = n;
        foreach (var key in new[] { "t3code", "t3codeChannel", "t3codeLimitResume", "opencodeBackgroundWatcher" })
            if (!current.ContainsKey(key) && (key == "t3codeChannel" ? StateJson.Text(config[key]) is "stable" or "nightly" : StateJson.Boolean(config[key]).HasValue)) patch[key] = config[key]?.DeepClone();
        if (patch.Count == 0) return null;
        SaveState(patch); return patch;
    }
    public JsonObject ReadMarkers() => UpdatePlanner.ReadMarkers(ReadInstallWide(), ReadState());
    public string ProvisionedCommit => StateJson.Text(ReadMarkers()["provisionedCommit"]) ?? "";
    public static int CountStale(IEnumerable<InstanceStateStore> stores) => stores.Count(s => UpdatePlanner.IsProvisionStale(s.ReadMarkers()));
    public ProvisionWatch CreateProvisionWatch(IClock clock, TimeSpan maximum) => new(this, clock, maximum);
}

public sealed class ProvisionWatch
{
    private readonly InstanceStateStore store;
    private readonly IClock clock;
    public string Baseline { get; }
    public DateTimeOffset Deadline { get; }
    public ProvisionWatch(InstanceStateStore store, IClock clock, TimeSpan maximum)
    {
        this.store = store; this.clock = clock; Baseline = store.ProvisionedCommit; Deadline = clock.UtcNow + maximum;
    }
    public string Current => store.ProvisionedCommit;
    public bool Done => Current is { Length: > 0 } current && current != Baseline || clock.UtcNow >= Deadline;
}
