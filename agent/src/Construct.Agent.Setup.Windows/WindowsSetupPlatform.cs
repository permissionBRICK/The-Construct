using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Construct.Agent.Setup.Windows;

/// <summary>The setup's seam on Windows: the current user's folders and Run key, and processes of this session.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSetupPlatform : ISetupPlatform
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public WindowsSetupPlatform()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LocalAppData = localAppData.Length > 0 ? localAppData : Path.GetTempPath();
        // As the agent finds its folder (AgentOptions.DefaultDataDirectory).
        AgentHome = Environment.GetEnvironmentVariable("CONSTRUCT_AGENT_HOME") is { Length: > 0 } home ? home : Path.Combine(LocalAppData, "Construct", "agent");
    }

    public string AgentHome { get; }

    public string LocalAppData { get; }

    public string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public void SetRunValue(string name, string? command)
    {
        if (command is null)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Start(string executable, string argument)
    {
        // Through the shell, as Start-Process does: the program is not tied to this console and outlives it.
        using var _ = Process.Start(new ProcessStartInfo(executable, argument)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? "",
        });
    }

    public bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<int> SessionProcesses(string name)
    {
        using var self = Process.GetCurrentProcess();
        var session = self.SessionId;
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == session) found.Add(process.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Gone in the meantime.
                }
            }
        }

        return found;
    }

    /// <summary>VS Code's command line, as install.ps1 finds it: on the search path, or in its two usual places.</summary>
    public string? FindVsCode() => VsCode.Find(Environment.GetEnvironmentVariable("PATH"), LocalAppData,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public async Task<int> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            return -1;
        }

        if (process is null) return -1;
        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(limit);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return -1;
            }

            return process.ExitCode;
        }
    }
}
