using System.Diagnostics;
using System.Runtime.Versioning;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class DesktopProcess : IDesktopProcess
{
    public Task OpenAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return Task.CompletedTask;
    }
    public Task StartAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.StandardInput is not null) throw new ArgumentException("Detached launches cannot carry standard input.");
        var info = new ProcessStartInfo(invocation.FileName) { UseShellExecute = false, CreateNoWindow = invocation.CreateNoWindow, WorkingDirectory = invocation.WorkingDirectory ?? "" };
        foreach (var arg in invocation.Arguments) info.ArgumentList.Add(arg);
        if (invocation.EnvironmentOverrides is {} environment) foreach (var (key, value) in environment)
            if (value is null) info.Environment.Remove(key); else info.Environment[key] = value;
        // Batch CLI shims need ShellExecute on Windows. Their URI argument is data.
        // ShellExecute ignores CreateNoWindow; code.cmd can briefly show a console.
        if (Path.GetExtension(invocation.FileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase)) info.UseShellExecute = true;
        using var process = Process.Start(info);
        return Task.CompletedTask;
    }
    public string? EnvironmentValue(string name) => Environment.GetEnvironmentVariable(name);
    public string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in new[] { ".exe", ".cmd", ".bat", "" })
            {
                var path = Path.Combine(directory.Trim('"'), executable + extension);
                if (File.Exists(path)) return path;
            }
        return null;
    }
}
