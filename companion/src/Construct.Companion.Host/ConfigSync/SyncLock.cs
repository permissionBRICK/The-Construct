using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Host.ConfigSync;
// The lock file format is shared with the extension and PowerShell engines; a dead owner pid breaks it.
public sealed class SyncLock(IStateFileSystem files, IProcessLiveness processes, IClock clock, string configDir)
{
    public const string LockFile = ".sync.lock";
    public const string ProvisionIntent = ".sync.provisioning";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    private bool Dead(string path)
    {
        try { var node = JsonNode.Parse(files.ReadFile(path) ?? []); var pid = node?["pid"]?.GetValue<int>(); return pid > 0 && processes.ProcessIsDefinitelyDead(pid.Value); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or IOException) { return false; }
    }
    public bool ProvisionSyncPending()
    {
        var path = Path.Combine(configDir, ProvisionIntent); var modified = files.LastWriteTime(path);
        if (modified == null) return false;
        if (!Dead(path) && clock.UtcNow - modified <= StaleAfter) return true;
        files.DeleteFile(path); return false;
    }
    public string? Acquire()
    {
        var path = Path.Combine(configDir, LockFile); var token = Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (files.WriteFileIfAbsent(path, Encoding.UTF8.GetBytes(ConfigSyncRules.Serialize(new { token, pid = processes.ProcessId, at = clock.UtcNow.ToString("O") })))) return token;
                var modified = files.LastWriteTime(path); if (modified == null) continue;
                if (!Dead(path) && clock.UtcNow - modified <= StaleAfter) return null;
                files.DeleteFile(path);
            }
            catch (IOException) { return null; }
        }
        return null;
    }
    public void Release(string? token)
    {
        try { var path = Path.Combine(configDir, LockFile); if (token != null && JsonNode.Parse(files.ReadFile(path) ?? [])?["token"]?.GetValue<string>() == token) files.DeleteFile(path); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
    }
}
