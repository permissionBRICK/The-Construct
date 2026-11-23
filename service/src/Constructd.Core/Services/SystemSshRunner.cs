using System.Globalization;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>The SSH or SCP call failed. The message names the operation and exit code, never a value.</summary>
public sealed class SshRunnerException(string message) : Exception(message), IConstructdError;

/// <summary>
/// <see cref="ISshRunner"/> over the system <c>ssh</c> and <c>scp</c>, through <see cref="IProcessRunner"/>
/// (an argv array, no local shell). The options are the PowerShell provisioner's
/// (<c>Provision-AgentVM.ps1</c> <c>$script:SshOpts</c>): batch mode, accept-new host keys in the
/// engine's own known-hosts file, a connect timeout, and keepalives so a guest that goes away ends
/// the call instead of hanging it.
/// </summary>
public sealed class SystemSshRunner(IProcessRunner processes) : ISshRunner
{
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(15);

    public async Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var args = SshArguments(target, command);
        // A null stdin is closed at once, which is what `ssh -n` gives the PowerShell calls.
        var result = await processes.RunAsync("ssh", args, standardInput ?? string.Empty, timeout, lines, cancellationToken)
            .ConfigureAwait(false);
        if (result.TimedOut) throw new SshRunnerException($"ssh to {target.Host}:{target.Port} timed out after {timeout}.");
        // ssh's own diagnostics (connection refused, host key, authentication) arrive on stderr.
        if (result.ExitCode == 255 && lines is not null)
        {
            foreach (var line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                lines.Report(line.TrimEnd('\r'));
        }

        return result.ExitCode;
    }

    public async Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync("scp", ScpArguments(target, localPath, remotePath), null, CopyTimeout, null, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
            throw new SshRunnerException($"scp to {target.Host}:{target.Port} failed (exit {(result.TimedOut ? "timeout" : result.ExitCode.ToString(CultureInfo.InvariantCulture))}).");
    }

    /// <summary>The <c>ssh</c> argument vector (public for the tests that pin it).</summary>
    public static IReadOnlyList<string> SshArguments(SshTarget target, string command)
    {
        var args = new List<string>();
        AddCommon(args, target);
        if (target.Port != 22) args.AddRange(["-p", target.Port.ToString(CultureInfo.InvariantCulture)]);
        args.Add($"{target.User}@{target.Host}");
        args.Add(command);
        return args;
    }

    /// <summary>The <c>scp</c> argument vector; an IPv6 literal is bracketed as scp requires.</summary>
    public static IReadOnlyList<string> ScpArguments(SshTarget target, string localPath, string remotePath)
    {
        var args = new List<string> { "-q" };
        AddCommon(args, target);
        if (target.Port != 22) args.AddRange(["-P", target.Port.ToString(CultureInfo.InvariantCulture)]);
        var host = target.Host.Contains(':') ? $"[{target.Host}]" : target.Host;
        args.Add(localPath);
        args.Add($"{target.User}@{host}:{remotePath}");
        return args;
    }

    private static void AddCommon(List<string> args, SshTarget target)
    {
        if (target.IdentityFile is { Length: > 0 } key) args.AddRange(["-i", key, "-o", "IdentitiesOnly=yes"]);
        args.AddRange(["-o", "StrictHostKeyChecking=accept-new"]);
        if (target.KnownHostsFile is { Length: > 0 } known) args.AddRange(["-o", "UserKnownHostsFile=" + known]);
        args.AddRange(["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4"]);
    }
}
