using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Core.State;

public sealed class HostState(IStateFileSystem files)
{
    public const string SettingsFile = ".construct-settings.json";
    public string? LocalAppData => new[] { FileSystemRoot.LocalAppData, FileSystemRoot.Temp }.Select(files.GetRoot).FirstOrDefault(r => !string.IsNullOrEmpty(r));
    public string? ConfigDirectory => LocalAppData is {} root ? Path.Combine(root, "The-Construct", "config") : null;
    public string? FindScriptsDirectory(string? root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        var candidates = new List<(string Path, DateTimeOffset Time)>();
        foreach (var first in Directories(Path.Combine(root, "The-Construct")))
            foreach (var dir in new[] { first }.Concat(Directories(first)))
                if (files.LastWriteTime(Path.Combine(dir, "Auto-Install.ps1")) is {} time) candidates.Add((dir, time));
        return candidates.OrderByDescending(c => c.Time).Select(c => c.Path).FirstOrDefault();
    }
    private IReadOnlyList<string> Directories(string path)
    {
        try { return files.EnumerateDirectories(path); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
    public string? ResolveScriptsDirectory(string? instanceDirectory = null, string? overrideDirectory = null, string? root = null)
    {
        foreach (var candidate in new[] { instanceDirectory, overrideDirectory })
            if (candidate is not null && StateJson.Trim(candidate) is { Length: > 0 } dir && files.DirectoryExists(dir)) return dir;
        return FindScriptsDirectory(root ?? LocalAppData);
    }
    public JsonObject ReadRawSettings(string? scriptsDirectory) => StateJson.ReadObject(files, scriptsDirectory is null ? null : Path.Combine(scriptsDirectory, SettingsFile)) ?? [];
    public void WriteRawSettings(string? scriptsDirectory, JsonObject value)
    {
        RequireDirectory(scriptsDirectory);
        files.WriteFileAtomic(Path.Combine(scriptsDirectory!, SettingsFile), StateJson.Bytes(value));
    }
    internal static void RequireDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("No Construct scripts directory resolved");
    }
    public static string SafeProfileName(JsonNode? name) => SafeProfileName(name is null ? null : StateJson.String(name));
    public static string SafeProfileName(string? name)
    {
        var s = StateJson.Trim(name ?? "");
        return s.Length == 0 || s.Contains("..", StringComparison.Ordinal) || Regex.IsMatch(s, "[\\\\/:*?\"<>|\\x00-\\x1f]") ? "" : s;
    }
    public JsonObject? ReadProjectProfile(string? directory, string? name)
    {
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) || name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(['/', '\\']) >= 0) return null;
        return StateJson.ReadObject(files, Path.Combine(directory, "projects", name + ".json"));
    }
    public string[] ListProjectProfiles(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return [];
        try { return files.EnumerateFiles(Path.Combine(directory, "projects")).Select(Path.GetFileName).Where(n => n is not null && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && n != "project.schema.json").Select(n => n![..^5]).Order(StringComparer.Ordinal).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
    private static string ProfilePath(string? directory, string? name)
    {
        RequireDirectory(directory);
        var safe = SafeProfileName(name);
        if (safe.Length == 0) throw new ArgumentException("Invalid project name");
        return Path.Combine(directory!, "projects", safe + ".json");
    }
    public void WriteProjectProfile(string? directory, string? name, JsonObject value)
    {
        var path = ProfilePath(directory, name); files.CreateDirectory(Path.GetDirectoryName(path)!); files.WriteFileAtomic(path, StateJson.Bytes(value));
    }
    public bool WriteProjectProfileIfAbsent(string? directory, string? name, JsonObject value)
    {
        var path = ProfilePath(directory, name); var parent = Path.GetDirectoryName(path)!; files.CreateDirectory(parent);
        if (files.EnumerateFiles(parent).Any(p => Path.GetFileName(p).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))) return false;
        return files.WriteFileIfAbsent(path, StateJson.Bytes(value));
    }
    public bool DeleteProjectProfile(string? directory, string? name)
    {
        var path = ProfilePath(directory, name); if (!files.FileExists(path)) return false; files.DeleteFile(path); return true;
    }
    public static JsonArray CleanSelection(JsonNode? names)
    {
        var values = names is JsonArray array ? array.Select(SafeProfileName).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal) : [];
        return new JsonArray(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }
    public JsonObject ReadSettings(string? directory) => SettingsMapping.MapToForm(ReadRawSettings(directory));
    public JsonObject SaveSettings(string? directory, JsonObject form) { var merged = StateJson.Merge(ReadRawSettings(directory), SettingsMapping.MapFromForm(form)); WriteRawSettings(directory, merged); return merged; }
    public JsonArray ReadSelectedProjects(string? directory) => CleanSelection(ReadRawSettings(directory)["projects"]);
    public bool HasPersistedSelection(string? directory) => ReadRawSettings(directory)["projects"] is JsonArray;
    public JsonObject SaveSelectedProjects(string? directory, JsonNode? names) { var merged = StateJson.Merge(ReadRawSettings(directory), new JsonObject { ["projects"] = CleanSelection(names) }); WriteRawSettings(directory, merged); return merged; }
}
