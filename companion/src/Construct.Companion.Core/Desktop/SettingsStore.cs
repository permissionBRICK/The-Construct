using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;

namespace Construct.Companion.Core.Desktop;

public sealed class SettingsStore(IFileSystem files, string path)
{
    private readonly object gate = new();
    public event Action<CompanionSettings>? Changed;
    public CompanionSettings Read()
    {
        lock (gate)
        {
            var data = files.ReadFile(path);
            if (data is null) return new();
            try { return Validate(JsonSerializer.Deserialize<CompanionSettings>(data, IpcJson.Options) ?? throw new JsonException()); }
            catch (JsonException) { throw new InvalidDataException("Companion settings are not valid JSON."); }
        }
    }
    public CompanionSettings Update(JsonObject patch)
    {
        lock (gate)
        {
            var current = JsonSerializer.SerializeToNode(Read(), IpcJson.Options)!.AsObject();
            foreach (var (key, value) in patch)
            {
                if (!current.ContainsKey(key)) throw new ArgumentException("Unknown Companion setting.");
                if (key is "windows" or "forwards" && value is JsonObject nested && current[key] is JsonObject original)
                    foreach (var (child, item) in nested) original[child] = item?.DeepClone();
                else current[key] = value?.DeepClone();
            }
            CompanionSettings settings;
            try { settings = Validate(current.Deserialize<CompanionSettings>(IpcJson.Options) ?? throw new JsonException()); }
            catch (JsonException) { throw new ArgumentException("Invalid Companion setting type."); }
            files.WriteFileAtomic(path, JsonSerializer.SerializeToUtf8Bytes(settings, IpcJson.Options));
            Changed?.Invoke(settings);
            return settings;
        }
    }
    private static CompanionSettings Validate(CompanionSettings value)
    {
        if (value.V != 1 || value.UiTheme is not ("" or "classic" or "terminal" or "native") || value.RepatchDelaySeconds < 0 ||
            value.MicDevice is null || value.ScriptsDir is null || value.Forwards is null || value.Forwards.HostLabel is null ||
            value.Windows is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
            throw new InvalidDataException("Companion settings contain unsupported values.");
        return value;
    }
    public WindowBounds? Bounds(string window)
    {
        var windows = Read().Windows;
        if (windows is not { ValueKind: JsonValueKind.Object } w || !w.TryGetProperty(window, out var bounds)) return null;
        try { return bounds.Deserialize<WindowBounds>(IpcJson.Options); } catch (JsonException) { return null; }
    }
    public void SaveBounds(string window, WindowBounds bounds) => Update(new JsonObject { ["windows"] = new JsonObject { [window] = JsonSerializer.SerializeToNode(bounds, IpcJson.Options) } });
}
