using System.Text;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Desktop;

// Call sites log event codes and exception types, never message payloads or argv.
// Writing is best-effort: a read-only or denied state directory must never turn a logged failure into a crash.
public enum DesktopLogEvent { Started, Stopped, ActivationFailed, BridgeFailed, WindowFailed, SettingsFailed, UnhandledException }
public sealed class RollingLog(IFileSystem files, IClock clock, string directory, int maximumBytes = 1024 * 1024)
{
    private readonly object gate = new();
    public string PathName => Path.Combine(directory, "companion.log");
    public void Write(DesktopLogEvent code, Exception? error = null)
    {
        try { WriteLine(code, error); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private void WriteLine(DesktopLogEvent code, Exception? error)
    {
        lock (gate)
        {
            var line = Encoding.UTF8.GetBytes($"{clock.UtcNow:O} {code}{(error is null ? "" : " " + error.GetType().Name)}\n");
            var current = files.ReadFile(PathName) ?? [];
            if (current.Length + line.Length > maximumBytes)
            {
                files.DeleteFile(PathName + ".4");
                for (var i = 3; i >= 0; i--)
                {
                    var source = i == 0 ? PathName : PathName + "." + i;
                    if (files.ReadFile(source) is {} contents) files.WriteFileAtomic(PathName + "." + (i + 1), contents);
                    else files.DeleteFile(PathName + "." + (i + 1));
                }
                current = [];
            }
            files.WriteFileAtomic(PathName, [.. current, .. line]);
        }
    }
}
