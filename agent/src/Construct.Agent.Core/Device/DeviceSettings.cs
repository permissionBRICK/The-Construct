using System.Text.Json;

namespace Construct.Agent.Device;

/// <summary>The device's own choices.</summary>
public sealed record DeviceSettingsValue
{
    /// <summary>The device's release channel (12A.3): <c>stable</c>, <c>nightly</c> or a manifest URL.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Installs a release by itself when idle (12.1, off by default).</summary>
    public bool AutoUpdate { get; init; }

    /// <summary>The VMs (<c>engine/vm</c>) whose microphone passthrough is on.</summary>
    public IReadOnlyList<string> Mic { get; init; } = [];
}

/// <summary>The device's own settings in <c>device.json</c> next to the state (0600).</summary>
public sealed class DeviceSettings(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public DeviceSettingsValue Read()
    {
        lock (_gate)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<DeviceSettingsValue>(File.ReadAllBytes(path), Json) ?? new() : new(); }
            catch (JsonException) { return new(); }
        }
    }

    public DeviceSettingsValue Update(Func<DeviceSettingsValue, DeviceSettingsValue> change)
    {
        lock (_gate)
        {
            var next = change(Read());
            PrivateFiles.Write(path, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            return next;
        }
    }
}
