using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Construct.Testing;

/// <summary>
/// One temporary directory per test process (compiled into each .NET test project): whatever a test leaves in
/// <see cref="Path.GetTempPath"/> lands there, child processes (git, bash, the agent executable) inherit it through
/// <c>TMPDIR</c>, and it is removed when the run ends. A run the test platform kills before its exit handler is done
/// is swept by the next run. The directory lies on <c>/dev/shm</c> where there is one, so the SQLite stores the
/// tests open commit without waiting for a disk flush (about 20 ms per commit on a busy disk).
/// </summary>
internal static class TestTemp
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsLinux()) return;
        var prefix = Assembly.GetExecutingAssembly().GetName().Name!.ToLowerInvariant() + "-";
        foreach (var parent in new[] { "/dev/shm", Path.GetTempPath() })
        {
            var root = Path.Combine(parent, prefix + Environment.ProcessId + "-" + Guid.NewGuid().ToString("n")[..8]);
            try { Directory.CreateDirectory(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Sweep(parent, prefix);
            Environment.SetEnvironmentVariable("TMPDIR", root);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(root);
            return;
        }
    }

    /// <summary>Removes the directories of earlier runs whose process is gone.</summary>
    private static void Sweep(string parent, string prefix)
    {
        foreach (var directory in Directory.EnumerateDirectories(parent, prefix + "*"))
        {
            var pid = Path.GetFileName(directory)[prefix.Length..].Split('-')[0];
            if (int.TryParse(pid, out var id) && id != Environment.ProcessId && !Running(id)) Delete(directory);
        }
    }

    private static bool Running(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
