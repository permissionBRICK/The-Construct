using System.Text.Json;
using Construct.Companion.Core.Desktop;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.State;
namespace Construct.Companion.Host.Ipc;

public sealed class IpcSettings(IFileSystem files, IpcEvents events, SettingsStore? store = null)
{
    private readonly object gate = new();
    // With a shared desktop store its Changed event publishes; without one Merge publishes itself.
    private readonly bool sharedEvents = Subscribe(store, events);
    private static bool Subscribe(SettingsStore? store, IpcEvents events)
    { if (store is null) return false; store.Changed += value => events.Companion(new { type = "settings", settings = value }); return true; }
    private bool corruptReported;
    public string Directory => Path.Combine(files.GetRoot(FileSystemRoot.LocalAppData) ?? files.GetRoot(FileSystemRoot.Temp) ?? throw new InvalidOperationException("No state root."), "The-Construct", "companion");
    public string PathName => Path.Combine(Directory, "settings.json");
    public CompanionSettings Read()
    {
        lock (gate)
        {
            var bytes = files.ReadFile(PathName);
            try { var value = bytes is null ? new CompanionSettings() : JsonSerializer.Deserialize<CompanionSettings>(bytes, IpcJson.Options) ?? new(); corruptReported = false; return value; }
            catch (JsonException) { if (!corruptReported) { corruptReported = true; new IpcLogs(files, this).Write("Invalid Companion settings; using defaults."); } return new(); }
        }
    }
    public CompanionSettings Merge(JsonObject patch)
    {
        lock (gate)
        {
            var current = JsonSerializer.SerializeToNode(Read(), IpcJson.Options)!.AsObject();
            foreach (var (key, value) in patch)
            {
                if (!current.ContainsKey(key) || key == "v") throw new IpcFailure(400, "invalidSettings", "Unknown or read-only setting.");
                if (key == "forwards" && value is JsonObject f)
                {
                    foreach (var (k, v) in f)
                    { if (k is not ("enabled" or "hostLabel")) throw new IpcFailure(400, "invalidSettings", "Unknown forward setting."); current[key]![k] = v?.DeepClone(); }
                }
                else current[key] = value?.DeepClone();
            }
            CompanionSettings settings;
            try { settings = current.Deserialize<CompanionSettings>(IpcJson.Options)!; }
            catch (JsonException) { throw new IpcFailure(400, "invalidSettings", "Invalid setting value."); }
            if (settings.UiTheme is not ("" or "classic" or "terminal" or "native") || settings.MicDevice is null || settings.ScriptsDir is null || settings.Forwards is null || settings.Forwards.HostLabel is null)
                throw new IpcFailure(400, "invalidSettings", "Invalid setting value.");
            settings = settings with { RepatchDelaySeconds = Math.Clamp(settings.RepatchDelaySeconds, 0, 600) };
            files.CreateDirectory(Directory);
            if (store is null) files.WriteFileAtomic(PathName, JsonSerializer.SerializeToUtf8Bytes(settings, IpcJson.Options));
            else { var validated = patch.DeepClone().AsObject(); if (validated.ContainsKey("repatchDelaySeconds")) validated["repatchDelaySeconds"] = settings.RepatchDelaySeconds; settings = store.Update(validated); }
            if (!sharedEvents) events.Companion(new { type = "settings", settings }); return settings;
        }
    }
}
