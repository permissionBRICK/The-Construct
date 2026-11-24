using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Construct.Testing;

/// <summary>
/// One temporary directory per test process (compiled into each .NET test project): whatever a test leaves in
/// <see cref="Path.GetTempPath"/> lands there, child processes (git, bash, the agent executable) inherit it through
/// <c>TMPDIR</c>, and it is removed when the run ends. A run the test platform kills before its exit handler is done
/// is swept by the next run once its exclusive lease file is released. The directory lies inside an owner-only
/// container on <c>/dev/shm</c> where there is one, so SQLite commits do not wait for a disk flush.
/// </summary>
internal static class TestTemp
{
    private const string LeaseName = ".construct-test-run.lock";
    private static FileStream? _lease;

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsLinux()) return;
        var uid = GetUid();
        var prefix = Assembly.GetExecutingAssembly().GetName().Name!.ToLowerInvariant() + "-";
        foreach (var parent in new[] { "/dev/shm", Path.GetTempPath() })
        {
            var container = Path.Combine(parent, $"construct-test-tmp-{uid}");
            if (!EnsureOwnerDirectory(container, uid)) continue;

            var root = Path.Combine(container, prefix + Environment.ProcessId + "-" + Guid.NewGuid().ToString("n")[..8]);
            FileStream? lease = null;
            try
            {
                Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                lease = new FileStream(Path.Combine(root, LeaseName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                var marker = Encoding.UTF8.GetBytes($"construct-test-tmp:{Environment.ProcessId}\n");
                lease.Write(marker);
                lease.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lease?.Dispose();
                Delete(root);
                continue;
            }

            _lease = lease;
            Sweep(container, prefix, root);
            Environment.SetEnvironmentVariable("TMPDIR", root);
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _lease?.Dispose(); } catch { }
                Delete(root);
            };
            return;
        }
    }

    private static bool EnsureOwnerDirectory(string path, uint uid)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return true;
            }

            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null) return false;
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
            return StatUid(path) == uid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes directories of earlier runs whose lease file is present and no longer locked.</summary>
    private static void Sweep(string container, string prefix, string ownRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(container, prefix + "*"))
        {
            if (string.Equals(directory, ownRoot, StringComparison.Ordinal)) continue;
            var leasePath = Path.Combine(directory, LeaseName);
            if (!File.Exists(leasePath)) continue;
            try
            {
                using var probe = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held by a live test process (including one in another PID namespace) or inaccessible.
                continue;
            }

            Delete(directory);
        }
    }

    private static uint GetUid()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && uint.TryParse(parts[1], out var uid)) return uid;
            }
        }
        catch (IOException) { }
        return 0;
    }

    private static uint? StatUid(string path)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stat", ["-c", "%u", path])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return p.ExitCode == 0 && uint.TryParse(output, out var uid) ? uid : null;
        }
        catch { return null; }
    }

    private static void Delete(string directory)
    {
        try { Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
