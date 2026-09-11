using System.Diagnostics;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
namespace Construct.Companion.Host.Runtime;

public sealed class HostProcesses : IProcessLiveness
{
    public int ProcessId => Environment.ProcessId;
    public bool ProcessIsDefinitelyDead(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    // ssh on PATH, then the Windows OpenSSH client; "ssh" when neither is visible.
    public static string SshExecutable(IFileSystem files) => Core.Runtime.SshExecutable.Resolve(files,
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), Environment.GetEnvironmentVariable("SystemRoot"), OperatingSystem.IsWindows());
}
