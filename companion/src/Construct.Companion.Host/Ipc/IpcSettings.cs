using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.State;
namespace Construct.Companion.Host.Ipc;

// The one owner of settings.json (§5): the PUT route, the dispatcher and the desktop windows all
// read and merge through it, and every change reaches the event stream and the tray once.
public sealed class IpcSettings(IFileSystem files, IpcEvents events)
{
    private readonly object gate = new();
    private bool corruptReported;
    public event Action<CompanionSettings>? Changed;
    public string Directory => Path.Combine(HostState.LocalAppDataRoot(files) ?? throw new InvalidOperationException("No state root."), "The-Construct", "companion");
    public string PathName => Path.Combine(Directory, "settings.json");
    // Unparseable JSON reads as defaults (logged once) and is replaced by the next merge; a parseable
    // file with an unsupported version or invalid values is refused and never rewritten.
    public CompanionSettings Read()
    {
        lock (gate)
        {
            var bytes = files.ReadFile(PathName);
            CompanionSettings? stored;
            try { stored = bytes is null ? new() : JsonSerializer.Deserialize<CompanionSettings>(bytes, IpcJson.Options); corruptReported = false; }
            catch (JsonException) { if (!corruptReported) { corruptReported = true; new IpcLogs(files, this).Write("Invalid Companion settings; using defaults."); } return new(); }
            return Validate(stored ?? new(), stored: true);
        }
    }
    // A partial merge: absent keys keep their value, "forwards" and "windows" merge one level deep.
    public CompanionSettings Merge(JsonObject patch)
    {
        lock (gate)
        {
            var current = JsonSerializer.SerializeToNode(Read(), IpcJson.Options)!.AsObject();
            foreach (var (key, value) in patch)
            {
                if (!current.ContainsKey(key) || key == "v") throw new IpcFailure(400, "invalidSettings", "Unknown or read-only setting.");
                if (key == "forwards" && value is JsonObject forwards)
                {
                    foreach (var (k, v) in forwards)
                    { if (k is not ("enabled" or "hostLabel")) throw new IpcFailure(400, "invalidSettings", "Unknown forward setting."); current[key]![k] = v?.DeepClone(); }
                }
                else if (key == "windows" && value is JsonObject windows && current[key] is JsonObject known)
                    foreach (var (k, v) in windows) known[k] = v?.DeepClone();
                else current[key] = value?.DeepClone();
            }
            CompanionSettings settings;
            try { settings = current.Deserialize<CompanionSettings>(IpcJson.Options)!; }
            catch (JsonException) { throw new IpcFailure(400, "invalidSettings", "Invalid setting value."); }
            // A client's out-of-range delay is clamped, as the extension's settings UI does; a stored one is refused.
            settings = Validate(settings with { RepatchDelaySeconds = Math.Clamp(settings.RepatchDelaySeconds, 0, 600) }, stored: false);
            files.CreateDirectory(Directory);
            files.WriteFileAtomic(PathName, JsonSerializer.SerializeToUtf8Bytes(settings, IpcJson.Options));
            events.Companion(new { type = "settings", settings });
            Changed?.Invoke(settings);
            return settings;
        }
    }
    private static CompanionSettings Validate(CompanionSettings value, bool stored)
    {
        if (value.V != 1) throw new IpcFailure(500, "unsupportedSettings", "Companion settings have an unsupported version; update Construct Companion.");
        var valid = (value.UiTheme == "" || WebViewDocument.IsKnownTheme(value.UiTheme)) && value.MicDevice is not null && value.ScriptsDir is not null
            && value.Forwards is { HostLabel: not null } && value.RepatchDelaySeconds >= 0 && value.Windows is not { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) };
        if (!valid) throw stored ? new IpcFailure(500, "invalidSettings", "Companion settings contain unsupported values.") : new IpcFailure(400, "invalidSettings", "Invalid setting value.");
        return value;
    }
    public WindowBounds? Bounds(string window)
    {
        if (Read().Windows is not { ValueKind: JsonValueKind.Object } windows || !windows.TryGetProperty(window, out var bounds)) return null;
        try { return bounds.Deserialize<WindowBounds>(IpcJson.Options); } catch (JsonException) { return null; }
    }
    public void SaveBounds(string window, WindowBounds bounds) => Merge(new JsonObject { ["windows"] = new JsonObject { [window] = JsonSerializer.SerializeToNode(bounds, IpcJson.Options) } });
    // Window geometry is a convenience: a read-only or refused settings file must never block closing a window.
    public Exception? TrySaveBounds(string window, WindowBounds bounds)
    {
        try { SaveBounds(window, bounds); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or IpcFailure) { return e; }
    }
}
