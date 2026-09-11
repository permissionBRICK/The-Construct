using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Core.Desktop;

// The dispatcher's ILauncher: plain opens and detached starts go straight through; elevated
// launches must be a prepared cmd.exe/Start-Process -Verb RunAs invocation from PowerShellLaunch.
public sealed class DesktopLauncher(IDesktopProcess desktop, IFileSystem files) : ILauncher
{
    public Task OpenAsync(string target, CancellationToken cancellationToken = default) => desktop.OpenAsync(target, cancellationToken);
    public Task StartDetachedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default) => desktop.StartAsync(invocation, cancellationToken);
    public Task LaunchElevatedAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.StandardInput is not null) throw new ArgumentException("Interactive launches cannot carry secrets.");
        if (!invocation.FileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Elevated launches require a prepared lifecycle invocation.");
        return desktop.StartAsync(invocation, cancellationToken);
    }
    public async Task<bool> OpenT3DesktopAsync(CancellationToken cancellationToken = default)
    {
        var local = files.GetRoot(FileSystemRoot.LocalAppData); if (local is null) return false;
        var directory = Path.Combine(local, "Programs", "t3code");
        var executable = files.EnumerateFiles(directory).FirstOrDefault(p => Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase));
        if (executable is null) return false;
        await StartDetachedAsync(new(executable, [], directory) { CreateNoWindow = true, EnvironmentOverrides = new Dictionary<string, string?> { ["ELECTRON_NO_ATTACH_CONSOLE"] = "1" } }, cancellationToken);
        return true;
    }
}
