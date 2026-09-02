namespace Constructd.Core.Abstractions;

/// <summary>What a finished child process left behind.</summary>
/// <param name="TimedOut">
/// The process did not exit within the timeout and was killed. <see cref="ExitCode"/> is then
/// meaningless.
/// </param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// The one way the service starts a child process. Every Windows-side operation — <c>powershell.exe</c>
/// for the Hyper-V driver, <c>wsl.exe</c> for the ISO build, <c>netsh.exe</c> for the port forwards —
/// goes through here, which buys two things:
///
/// <list type="bullet">
/// <item><b>Security.</b> The arguments are an <em>argv array</em>, never a command string: nothing
/// the caller passes can be reinterpreted as a separate argument, a redirection or a second command.
/// Implementations must not go through a shell.</item>
/// <item><b>Testability.</b> A recording implementation lets the tests assert the exact file and the
/// exact argument vector on a machine that has none of those programs (this repo's tests run on
/// Linux).</item>
/// </list>
///
/// Implementations never log or embed the argument vector: it can carry a VM's seed password
/// (<c>VM_PASS=…</c> for the ISO build).
/// </summary>
public interface IProcessRunner
{
    /// <param name="fileName">Executable to start; resolved through <c>PATH</c> when not rooted.</param>
    /// <param name="arguments">Argument vector, one element per argument — never a joined string.</param>
    /// <param name="standardInput">Written to the child's stdin and then closed; null closes it immediately.</param>
    /// <param name="timeout">The child is killed (with its tree) when it outlives this.</param>
    /// <param name="standardOutputLines">
    /// Receives each stdout line as it arrives, for progress streaming. The full output is returned in
    /// <see cref="ProcessResult.StandardOutput"/> regardless.
    /// </param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        IProgress<string>? standardOutputLines,
        CancellationToken cancellationToken);
}
