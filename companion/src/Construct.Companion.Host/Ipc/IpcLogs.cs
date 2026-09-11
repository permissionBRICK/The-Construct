using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Ipc;

// Accept only already-sanitized operational messages, never exception bodies or request data.
public sealed class IpcLogs(IFileSystem files, IpcSettings settings)
{
    private readonly object gate = new();
    public string PathName => Path.Combine(settings.Directory, "logs", "companion.log");
    public string[] Read(int count)
    { lock (gate) return System.Text.Encoding.UTF8.GetString(files.ReadFile(PathName) ?? []).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(Math.Clamp(count, 0, 10000)).ToArray(); }
    public void Failure(string operation, Exception error) => Write(operation + " failed (" + error.GetType().Name + "): " + Redact(error.Message));
    // Exception messages may quote headers or URLs; strip anything that looks like a credential and cap the length.
    public static string Redact(string message)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(message ?? "", @"(?i)\b(bearer|vmtoken|token|password)[=: ]+\S+", "$1 [redacted]");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b[0-9a-f]{48,}\b", "[redacted]").Replace("\r", " ").Replace("\n", " ");
        return text.Length <= 300 ? text : text[..300] + "…";
    }
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
