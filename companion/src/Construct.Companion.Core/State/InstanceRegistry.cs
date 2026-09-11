using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Core.State;

public sealed class InstanceRegistry
{
    public Dictionary<string, JsonObject> ByName { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);
    public string DefaultInstance { get; private set; } = Instances.DefaultName;
    public List<string> Problems { get; } = [];
    public string? FilePath { get; private set; }
    public bool Exists { get; private set; }
    public bool Synthesized => !Exists && ByName.Count == 1 && ByName.ContainsKey(Instances.DefaultName);
    public IReadOnlyList<JsonObject> List() => ByName.OrderBy(p => p.Key == DefaultInstance ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonObject)p.Value.DeepClone()).ToArray();
    public JsonObject Resolve(string? name) => (JsonObject)(ByName.GetValueOrDefault(StateJson.Trim(name ?? "")) ?? ByName.GetValueOrDefault(DefaultInstance) ?? ByName.GetValueOrDefault(Instances.DefaultName) ?? Instances.DeriveDefaults(Instances.DefaultName)).DeepClone();
    public JsonObject ResolveActive(string? setting = null, string? workspaceValue = null)
    {
        var problems = new JsonArray();
        foreach (var (value, source) in new[] { (setting, "setting"), (workspaceValue, "workspace") })
        {
            var name = StateJson.Trim(value ?? ""); if (name.Length == 0) continue;
            if (ByName.TryGetValue(name, out var found)) return Result(found, source);
            problems.Add($"instance \"{name}\" ({source}) is not in the registry — skipped");
        }
        return Result(Resolve(null), "default");
        JsonObject Result(JsonObject instance, string source)
        {
            var result = new JsonObject { ["instance"] = instance.DeepClone(), ["name"] = instance["name"]?.DeepClone(), ["source"] = source };
            if (problems.Count > 0) { result["problem"] = problems[0]?.DeepClone(); result["problems"] = problems; }
            return result;
        }
    }
    public static InstanceRegistry Parse(string? text)
    {
        var registry = new InstanceRegistry(); var problems = registry.Problems; JsonObject? doc = null;
        if (!string.IsNullOrEmpty(StateJson.Trim(text ?? "")))
        {
            try { doc = JsonNode.Parse(StateJson.StripBom(text!)) as JsonObject; if (doc is null) problems.Add("instances.json must contain a JSON object"); }
            catch (JsonException) { problems.Add("instances.json is not valid JSON"); }
        }
        if (doc?["version"] is {} version && StateJson.Number(version) != 1)
        {
            problems.Add($"instances.json has version {StateJson.Quote(version)}; this Construct only understands version 1 — ignoring the file and using the default instance (update Construct)"); doc = null;
        }
        if (doc is not null)
        {
            var bag = doc["instances"] as JsonObject;
            if (doc["instances"] is not null && bag is null) problems.Add("instances.json: \"instances\" must be an object");
            if (bag is not null) foreach (var (name, node) in bag)
            {
                if (!Instances.IsValidName(name)) { problems.Add($"instance name \"{name}\" is invalid ({Instances.NameRule}) — skipped"); continue; }
                if (node is null) { registry.Removed.Add(name); continue; }
                if (node is not JsonObject entry) { problems.Add($"instance \"{name}\" is not an object — skipped"); continue; }
                foreach (var key in new[] { "vmName", "sshHost", "vmHost", "hostAlias", "keyName", "configBranch", "scriptsDir", "owner", "publicHost" })
                    if (entry[key] is not null && StateJson.Text(entry[key]) is null) problems.Add($"instance \"{name}\": \"{key}\" must be a string — using the derived default");
                if (StateJson.Nonempty(entry["publicHost"]) is {} pub && !Instances.IsHostEndpoint(pub))
                { problems.Add($"instance \"{name}\": \"publicHost\" {StateJson.Quote(JsonValue.Create(pub))} is not a host name or IP address — ignored"); entry["publicHost"] = null; }
                var backendBad = Instances.BackendProblems(entry["backend"]); var backend = StateJson.Nonempty(entry["backend"]);
                if (backendBad.Length == 0 && backend is not null and not ("hyperv-local" or "hyperv-remote")) problems.Add($"instance \"{name}\" has an unknown backend {StateJson.Quote(JsonValue.Create(backend))} — this Construct has no driver for it, so rebuild/checkpoint actions are unavailable for it (update Construct if a newer version created it)");
                if (entry["sshPort"] is not null && Instances.CoercePort(entry["sshPort"]) is null) problems.Add($"instance \"{name}\" has an invalid sshPort — using 22");
                var service = entry["service"] as JsonObject;
                if (entry["service"] is not null && service is null) problems.Add($"instance \"{name}\": \"service\" must be an object — ignored");
                else if (service?["url"] is not null && StateJson.Text(service["url"]) is null) problems.Add($"instance \"{name}\": \"service.url\" must be a string — the service entry is ignored");
                else if (service?["auth"] is not null && StateJson.Text(service["auth"]) is not ("token" or "negotiate")) problems.Add($"instance \"{name}\": unknown service auth {StateJson.Quote(service["auth"])} — using negotiate");
                var instance = Instances.DeriveDefaults(name, entry);
                var bad = backendBad.Concat(Instances.IdentityProblems(instance, entry)).Concat(Instances.LocalIdentityProblems(instance)).Concat(Instances.RemoteIdentityProblems(instance, entry)).ToArray();
                if (bad.Length > 0) { problems.Add($"instance \"{name}\": {string.Join("; ", bad)} — skipped"); continue; }
                registry.ByName[name] = instance;
            }
            var collisions = registry.Collisions(); problems.AddRange(collisions.Problems); foreach (var name in collisions.Drop) registry.ByName.Remove(name);
            if (StateJson.Nonempty(doc["defaultInstance"]) is {} wanted)
            {
                if (!Instances.IsValidName(wanted)) problems.Add($"defaultInstance \"{wanted}\" is not a valid instance name — using \"agent-vm\"");
                else if (!registry.ByName.ContainsKey(wanted)) problems.Add($"defaultInstance \"{wanted}\" has no entry in \"instances\" — using \"agent-vm\"");
                else registry.DefaultInstance = wanted;
            }
        }
        if (!registry.ByName.ContainsKey(Instances.DefaultName) && !registry.Removed.Contains(Instances.DefaultName)) registry.ByName[Instances.DefaultName] = Instances.DeriveDefaults(Instances.DefaultName);
        registry.RepairDefault(); return registry;
    }
    private void RepairDefault()
    {
        if (!ByName.ContainsKey(DefaultInstance)) DefaultInstance = ByName.ContainsKey(Instances.DefaultName) || ByName.Count == 0 ? Instances.DefaultName : ByName.Keys.Order(StringComparer.Ordinal).First();
    }
    public (string[] Problems, HashSet<string> Drop) Collisions()
    {
        var problems = new List<string>(); var drop = new HashSet<string>(StringComparer.Ordinal); var names = ByName.Keys.Order(StringComparer.Ordinal).ToArray();
        var fields = new[] { "vmName", "sshHost/sshPort", "hostAlias", "keyName", "configBranch" };
        string Show(JsonObject i, string f) => f == "sshHost/sshPort" ? StateJson.String(i["vmHost"]) + ":" + (Instances.CoercePort(i["sshPort"]) ?? 22) : StateJson.String(i[f]);
        bool Same(JsonObject a, JsonObject b, string f) => string.Equals(Show(a, f), Show(b, f), StringComparison.OrdinalIgnoreCase);
        var def = Instances.DeriveDefaults(Instances.DefaultName);
        foreach (var name in names.Where(n => n != Instances.DefaultName)) foreach (var f in fields)
            if (Same(ByName[name], def, f)) { problems.Add($"instance \"{name}\": {f} {StateJson.Quote(JsonValue.Create(Show(ByName[name], f)))} belongs to the default instance \"agent-vm\" — skipped"); drop.Add(name); break; }
        for (var a = 0; a < names.Length; a++) for (var b = a + 1; b < names.Length; b++) foreach (var f in fields)
            if (Same(ByName[names[a]], ByName[names[b]], f)) { problems.Add($"instances \"{names[a]}\" and \"{names[b]}\" share the same {f} {StateJson.Quote(JsonValue.Create(Show(ByName[names[a]], f)))} — both skipped"); drop.Add(names[a]); drop.Add(names[b]); break; }
        return (problems.ToArray(), drop);
    }
    public static JsonObject ToFileEntry(JsonObject instance)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "backend", "vmName", "sshHost", "sshPort", "hostAlias", "keyName", "configBranch", "scriptsDir", "service", "owner" }) result[key] = instance[key == "sshHost" ? "vmHost" : key]?.DeepClone();
        if (StateJson.Nonempty(instance["publicHost"]) is {} pub) result["publicHost"] = pub;
        return result;
    }
    public JsonObject ToFileDocument()
    {
        var bag = new JsonObject(); foreach (var instance in List()) bag[StateJson.String(instance["name"])] = ToFileEntry(instance);
        foreach (var name in Removed.Order(StringComparer.Ordinal)) if (!bag.ContainsKey(name)) bag[name] = null;
        return new JsonObject { ["version"] = 1, ["defaultInstance"] = DefaultInstance, ["instances"] = bag };
    }
    public static InstanceRegistry Load(IStateFileSystem files, string? path = null)
    {
        path ??= new HostState(files).LocalAppData is {} root ? Path.Combine(root, "The-Construct", "instances.json") : null;
        byte[]? bytes = null; var failed = false;
        try { if (path is not null) bytes = files.ReadFile(path); }
        catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
        var registry = Parse(bytes is null ? null : Encoding.UTF8.GetString(bytes)); registry.FilePath = path; registry.Exists = bytes is not null;
        if (failed) registry.Problems.Insert(0, "instances.json could not be read — using the default instance");
        return registry;
    }
    public void Save(IFileSystem files, string? path = null)
    {
        path ??= FilePath; if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("No instances.json path resolved");
        files.CreateDirectory(Path.GetDirectoryName(path)!); files.WriteFileAtomic(path, StateJson.Bytes(ToFileDocument()));
    }
    private InstanceRegistry Clone()
    {
        var next = new InstanceRegistry { DefaultInstance = DefaultInstance, FilePath = FilePath, Exists = Exists };
        foreach (var (name, instance) in ByName) next.ByName[name] = (JsonObject)instance.DeepClone(); next.Removed.UnionWith(Removed); return next;
    }
    private static JsonObject Validate(string name, JsonObject? raw)
    {
        var instance = Instances.DeriveDefaults(name, raw);
        var bad = Instances.BackendProblems(raw?["backend"]).Concat(Instances.IdentityProblems(instance, raw)).Concat(Instances.LocalIdentityProblems(instance)).Concat(Instances.RemoteIdentityProblems(instance, raw)).ToArray();
        if (bad.Length > 0) throw new ArgumentException($"Instance \"{name}\": {string.Join("; ", bad)}"); return instance;
    }
    public InstanceRegistry Add(string name, JsonObject? entry)
    {
        if (!Instances.IsValidName(name)) throw new ArgumentException($"Invalid instance name \"{name}\"");
        if (ByName.ContainsKey(name)) throw new ArgumentException($"Instance \"{name}\" already exists");
        var next = Clone(); next.ByName[name] = Validate(name, entry); next.AssertNoCollisions(); return next;
    }
    public InstanceRegistry Update(string name, JsonObject patch)
    {
        if (!ByName.TryGetValue(name, out var current)) throw new ArgumentException($"Unknown instance \"{name}\"");
        var next = Clone(); next.ByName[name] = Validate(name, StateJson.Merge(ToFileEntry(current), patch)); next.AssertNoCollisions(); return next;
    }
    private void AssertNoCollisions() { var problems = Collisions().Problems; if (problems.Length > 0) throw new ArgumentException(problems[0]); }
    public InstanceRegistry Remove(string name)
    {
        if (!ByName.ContainsKey(name)) throw new ArgumentException($"Unknown instance \"{name}\"");
        if (ByName.Count <= 1) throw new InvalidOperationException($"\"{name}\" is the only instance on this PC and cannot be removed");
        var next = Clone(); next.ByName.Remove(name); if (name == Instances.DefaultName) next.Removed.Add(name); next.RepairDefault(); return next;
    }
}
