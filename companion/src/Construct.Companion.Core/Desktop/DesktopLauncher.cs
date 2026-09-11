using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;

namespace Construct.Companion.Core.Desktop;

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

    public string? FindVsCodeCli()
    {
        if (desktop.FindOnPath("code") is {} found) return found;
        foreach (var (root, relative) in new[] { (files.GetRoot(FileSystemRoot.LocalAppData), "Programs/Microsoft VS Code/bin/code.cmd"), (desktop.EnvironmentValue("ProgramFiles"), "Microsoft VS Code/bin/code.cmd"), (desktop.EnvironmentValue("ProgramFiles(x86)"), "Microsoft VS Code/bin/code.cmd") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (files.FileExists(candidate)) return candidate;
        }
        return null;
    }
    public Task OpenVsCodeAsync(string alias, string path = "/root", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(alias) || alias.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) throw new ArgumentException("Invalid SSH alias.");
        var target = "vscode-remote://ssh-remote+" + alias + "/" + path.TrimStart('/');
        var cli = FindVsCodeCli();
        return cli is null ? OpenAsync("vscode://vscode-remote/ssh-remote+" + alias + "/" + path.TrimStart('/'), cancellationToken) :
            StartDetachedAsync(new(cli, ["--folder-uri", target]), cancellationToken);
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
