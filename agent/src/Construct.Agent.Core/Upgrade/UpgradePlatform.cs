using System.Diagnostics;
using Construct.Agent.Protection;

namespace Construct.Agent.Upgrade;

/// <summary>A VM the converter reaches over SSH with the old client's key, as the old client did (as root).</summary>
public sealed record LegacyVmTarget(string Instance, string Host, int Port, string KeyFile);

/// <summary>What a command on a VM answered: its exit code and its standard output (never a secret).</summary>
public sealed record VmShellResult(int Exit, string Output);

/// <summary>
/// The OS parts of the one-time upgrade (11.6, 13.2): opening the old client's DPAPI-protected files,
/// the old writers that must be closed, one elevated program run, and SSH to a VM. Tests pass a fake;
/// on Linux the old files cannot be opened (<see cref="LegacyProtection"/> is null).
/// </summary>
public interface IUpgradePlatform
{
    /// <summary>The old client's protection (DPAPI, current user, no entropy) or null where it is not available.</summary>
    IDataProtection? LegacyProtection { get; }

    /// <summary>The old client's base directory (<c>%LOCALAPPDATA%</c>); null when there is none.</summary>
    string? LocalAppData { get; }

    /// <summary>The user's home (for <c>~/.ssh</c>).</summary>
    string Home { get; }

    /// <summary>The old writers still running, as the user would name them: VS Code, the old Companion. Empty when none.</summary>
    IReadOnlyList<string> OldWriters();

    /// <summary>Runs <paramref name="program"/> with one elevation prompt and no window, and waits for it; its exit code.</summary>
    Task<int> RunElevatedAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>Whether this device can run an engine (5.3): Windows only.</summary>
    bool CanHostEngine { get; }

    /// <summary>Restarts the OS now, without a dialog of its own (the user clicked Restart now).</summary>
    Task RestartWindowsAsync(CancellationToken ct);

    /// <summary>Runs <paramref name="command"/> on a VM as root, <paramref name="stdin"/> on its standard input.</summary>
    Task<VmShellResult> RunOnVmAsync(LegacyVmTarget target, IReadOnlyList<string> command, byte[]? stdin, CancellationToken ct);
}

/// <summary>
/// The upgrade's platform where nothing Windows-specific is available (Linux): no DPAPI, no elevation,
/// VS Code found by process name, SSH through the <c>ssh</c> client. The Windows shell derives from it.
/// </summary>
public class DefaultUpgradePlatform(string knownHostsFile) : IUpgradePlatform
{
    public virtual IDataProtection? LegacyProtection => null;

    public virtual string? LocalAppData => Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? local : null;

    public virtual string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public virtual string SshExecutable => "ssh";

    public virtual IReadOnlyList<string> OldWriters()
    {
        var found = new List<string>();
        var code = Count("code") + Count("Code");
        if (code > 0) found.Add("VS Code");
        if (Count("ConstructCompanion") > 0) found.Add("Construct Companion");
        return found;
    }

    protected static int Count(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length;
    }

    public virtual Task<int> RunElevatedAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct) =>
        throw new AgentFailure(501, "not-available", "Installing the engine needs Windows.");

    public virtual bool CanHostEngine => false;

    public virtual Task RestartWindowsAsync(CancellationToken ct) => throw new AgentFailure(501, "not-available", "Restarting needs Windows.");

    public virtual async Task<VmShellResult> RunOnVmAsync(LegacyVmTarget target, IReadOnlyList<string> command, byte[]? stdin, CancellationToken ct)
    {
        var info = new ProcessStartInfo(SshExecutable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     "-i", target.KeyFile, "-p", target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "IdentitiesOnly=yes",
                     "-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=" + knownHostsFile,
                     "-l", "root", target.Host, "--",
                 })
            info.ArgumentList.Add(arg);
        // The command is fixed words (no value from a file); ssh joins them for the remote shell.
        foreach (var word in command) info.ArgumentList.Add(word);
        using var process = Process.Start(info) ?? throw new AgentFailure(502, "ssh-failed", "ssh did not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (stdin is not null) await process.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            return new VmShellResult(255, "");
        }

        await error.ConfigureAwait(false);
        return new VmShellResult(process.ExitCode, await output.ConfigureAwait(false));
    }
}
