namespace Constructd.Core.Abstractions;

/// <summary>
/// A command on a guest that keeps its stdin open: the engine writes to it while the command runs
/// (the repair agent, design 11.5). <see cref="ISshRunner"/> closes stdin after writing it, which a
/// conversation cannot use. Tests replace the interface.
/// </summary>
public interface IGuestSessionRunner
{
    /// <summary>
    /// Starts <paramref name="command"/> through the guest's login shell. Each stdout line goes to
    /// <paramref name="onLine"/> as it arrives, in order; the command's stderr is the caller's to merge.
    /// </summary>
    IGuestSession Start(SshTarget target, string command, Action<string> onLine);
}

/// <summary>A running guest command. Disposing it closes stdin, waits a moment and then ends the connection.</summary>
public interface IGuestSession : IAsyncDisposable
{
    /// <summary>Writes one line to the command's stdin.</summary>
    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Completes when the command (or the connection) has ended and its output was delivered.</summary>
    Task Exited { get; }
}
