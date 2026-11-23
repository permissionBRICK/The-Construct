namespace Constructd.Core.Abstractions;

/// <summary>
/// Where the engine dials a guest over SSH. <paramref name="IdentityFile"/> null uses the engine
/// account's default identities. <paramref name="KnownHostsFile"/> is the engine's own file
/// (<c>StrictHostKeyChecking=accept-new</c>, as the PowerShell provisioner does with its file).
/// </summary>
public sealed record SshTarget(string Host, int Port, string User, string? IdentityFile, string? KnownHostsFile)
{
    public bool IsRoot => User == "root";
}

/// <summary>
/// Runs commands on a guest and copies files to it — the engine's port of <c>Invoke-SshStream</c>,
/// <c>Send-GuestSecret</c> and <c>Invoke-Scp</c> (<c>Provision-AgentVM.ps1</c>) and of the
/// Companion's <c>SshArgs</c>. Implementations use the system <c>ssh</c> and <c>scp</c>; tests replace
/// the interface.
///
/// Secrets go in <paramref name="standardInput"/> only, never in <paramref name="command"/>: the
/// command is an argument of the local <c>ssh</c> process and of the remote shell.
/// </summary>
public interface ISshRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> through the remote login shell. Each output line is passed to
    /// <paramref name="lines"/> as it arrives (stdout; the caller merges stderr with <c>2&gt;&amp;1</c>
    /// where it wants it). <paramref name="standardInput"/> is written and closed; null gives the
    /// remote command an empty stdin. Returns the remote exit code; 255 is ssh's own failure.
    /// </summary>
    Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines,
        TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Copies a local file to <paramref name="remotePath"/>. Throws on failure.</summary>
    Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken cancellationToken);
}
