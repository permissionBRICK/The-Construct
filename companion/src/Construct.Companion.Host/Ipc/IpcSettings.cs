using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.State;
namespace Construct.Companion.Host.Ipc;

public sealed class IpcFailure(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed class IpcSettings(IFileSystem files, IpcEvents events)
{
    private readonly object gate = new();
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
            if (settings.UiTheme is not ("" or "classic" or "terminal" or "native") || settings.MicDevice is null || settings.ScriptsDir is null || settings.Forwards.HostLabel is null)
                throw new IpcFailure(400, "invalidSettings", "Invalid setting value.");
            settings = settings with { RepatchDelaySeconds = Math.Clamp(settings.RepatchDelaySeconds, 0, 600) };
            files.CreateDirectory(Directory);
            files.WriteFileAtomic(PathName, JsonSerializer.SerializeToUtf8Bytes(settings, IpcJson.Options));
            events.Companion(new { type = "settings", settings }); return settings;
        }
    }
}
// Accept only already-sanitized operational messages, never exception bodies or request data.
public sealed class IpcLogs(IFileSystem files, IpcSettings settings)
{
    private readonly object gate = new();
    public string PathName => Path.Combine(settings.Directory, "logs", "companion.log");
    public string[] Read(int count)
    { lock (gate) return System.Text.Encoding.UTF8.GetString(files.ReadFile(PathName) ?? []).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(Math.Clamp(count, 0, 10000)).ToArray(); }
    public void Failure(string operation, Exception error) => Write(operation + " failed (" + error.GetType().Name + ").");
    public void Write(string message)
    {
        lock (gate)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(files.ReadFile(PathName) ?? []) + message + "\n");
            files.CreateDirectory(Path.GetDirectoryName(PathName)!);
            if (data.Length > 1048576)
            {
                for (var i = 4; i >= 1; i--) { var old = files.ReadFile(i == 1 ? PathName : PathName + "." + (i - 1)); if (old is not null) files.WriteFileAtomic(PathName + "." + i, old); }
                data = System.Text.Encoding.UTF8.GetBytes(message + "\n");
            }
            files.WriteFileAtomic(PathName, data);
        }
    }
}
