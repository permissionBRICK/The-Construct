using System.ComponentModel;
using System.Diagnostics;
using Construct.Agent.Upgrade;

namespace Construct.Agent.Windows;

/// <summary>
/// The one-time upgrade on Windows (13.2): the old client's files open through DPAPI (current user, no entropy,
/// as <c>lib/AgentVm.Remote.ps1</c> and the Companion's vault wrote them), the engine installer runs with one
/// UAC prompt, ssh is Windows' OpenSSH client, and the old Companion's single-instance mutex is held.
/// </summary>
internal sealed class WindowsUpgradePlatform(string knownHostsFile) : DefaultUpgradePlatform(knownHostsFile)
{
    private const string CompanionMutex = @"Local\ConstructCompanion";
    private volatile bool _holdsCompanionMutex;

    public override Protection.IDataProtection? LegacyProtection { get; } = new DpapiProtection();

    public override string? LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public override string SshExecutable
    {
        get
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            return File.Exists(system) ? system : "ssh.exe";
        }
    }

    /// <summary>
    /// Takes the Companion's mutex on a thread of its own and keeps it until the process ends. While a Companion
    /// runs, the thread keeps trying; <see cref="OldWriters"/> names the Companion until it has the mutex.
    /// </summary>
    public void HoldCompanionMutex()
    {
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, CompanionMutex);
            while (true)
            {
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromSeconds(2))) break;
                }
                catch (AbandonedMutexException)
                {
                    break;
                }
            }

            _holdsCompanionMutex = true;
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true, Name = "companion-mutex" };
        thread.Start();
    }

    public override IReadOnlyList<string> OldWriters()
    {
        var found = new List<string>();
        if (Count("Code") > 0) found.Add("VS Code");
        if (!_holdsCompanionMutex || Count("ConstructCompanion") > 0) found.Add("Construct Companion");
        return found;
    }

    public override async Task<int> RunElevatedAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        // runas needs the shell: the arguments are quoted here (paths and fixed switches, no secret).
        var info = new ProcessStartInfo(program, string.Join(' ', arguments.Select(Quote))) { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var process = Process.Start(info) ?? throw new AgentFailure(502, "engine-install-failed", "The installer did not start.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new AgentFailure(409, "elevation-declined", "The administrator prompt was declined; the engine was not installed.");
        }
    }

    private static string Quote(string value) => value.Length > 0 && !value.Any(c => c is ' ' or '"' or '\t') ? value : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
