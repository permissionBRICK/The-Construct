using System.Diagnostics;
using System.Text;
using Constructd.Core.Abstractions;

namespace Constructd.Windows.Process;

/// <summary>
/// The real <see cref="IProcessRunner"/>: <see cref="System.Diagnostics.Process"/> with
/// <c>UseShellExecute = false</c> and an <see cref="ProcessStartInfo.ArgumentList"/>.
///
/// No command string is ever built. On Windows the runtime quotes each element of
/// <see cref="ProcessStartInfo.ArgumentList"/> per the CommandLineToArgvW rules, so an argument
/// containing spaces, quotes or an <c>&amp;</c> stays exactly one argument of the target program and
/// can never turn into a second command — the property this service depends on for every value that
/// reaches <c>powershell.exe</c>, <c>wsl.exe</c> or <c>netsh.exe</c>.
///
/// The runner never logs the argument vector: it can carry a VM's seed password.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        IProgress<string>? standardOutputLines,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();

        // The child is killed when it outlives the timeout; the caller's own cancellation is kept
        // distinguishable from that so a timeout is reported as one instead of as a cancellation.
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        var stdout = ReadAsync(process.StandardOutput, standardOutputLines, combined.Token);
        var stderr = ReadAsync(process.StandardError, progress: null, combined.Token);

        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), combined.Token)
                    .ConfigureAwait(false);
            }

            process.StandardInput.Close();

            await process.WaitForExitAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(
                ExitCode: -1,
                await SafeAsync(stdout).ConfigureAwait(false),
                await SafeAsync(stderr).ConfigureAwait(false),
                TimedOut: true);
        }

        return new ProcessResult(
            process.ExitCode,
            await SafeAsync(stdout).ConfigureAwait(false),
            await SafeAsync(stderr).ConfigureAwait(false),
            TimedOut: false);
    }

    private static async Task<string> ReadAsync(
        StreamReader reader,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            text.Append(line).Append('\n');
            progress?.Report(line);
        }

        return text.ToString();
    }

    /// <summary>Whatever the reader managed to collect; a killed child can cut a stream short.</summary>
    private static async Task<string> SafeAsync(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // The whole tree: powershell.exe and wsl.exe both start children of their own.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (NotSupportedException)
        {
            // Remote process; cannot happen for a child we started.
        }
    }
}
