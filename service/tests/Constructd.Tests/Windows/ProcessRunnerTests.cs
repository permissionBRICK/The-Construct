using Constructd.Windows.Process;

namespace Constructd.Tests.Windows;

/// <summary>
/// The real process runner, exercised against small POSIX binaries. Everything the Windows
/// implementations issue goes through it, so the properties asserted here — an argument vector that no
/// shell ever sees, a timeout that actually kills, and stdout streamed as it arrives — are what keeps
/// a VM name or a seed password from turning into a second command.
///
/// Skipped on Windows, where these particular binaries do not exist; the code path is identical.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static bool Posix => !OperatingSystem.IsWindows() && File.Exists("/bin/echo");

    [Fact]
    public async Task Arguments_reach_the_child_verbatim_and_are_never_interpreted_by_a_shell()
    {
        if (!Posix)
        {
            return;
        }

        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "/bin/echo",
            ["one two", "$(id -u)", "a;rm -rf /", "*"],
            standardInput: null,
            TimeSpan.FromSeconds(30),
            standardOutputLines: null,
            CancellationToken.None);

        Assert.True(result.Succeeded);

        // No word splitting, no command substitution, no glob expansion: one argument stays one
        // argument, whatever it contains.
        Assert.Equal("one two $(id -u) a;rm -rf / *", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task Standard_input_is_written_and_then_closed()
    {
        if (!Posix)
        {
            return;
        }

        var runner = new ProcessRunner();

        // cat would hang forever if stdin were left open.
        var result = await runner.RunAsync(
            "/bin/cat",
            [],
            "hello from the service",
            TimeSpan.FromSeconds(30),
            standardOutputLines: null,
            CancellationToken.None);

        Assert.Equal("hello from the service", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task Stdout_lines_are_reported_as_they_arrive()
    {
        if (!Posix)
        {
            return;
        }

        var runner = new ProcessRunner();
        var progress = new ProgressSink();

        var result = await runner.RunAsync(
            "/bin/sh",
            ["-c", "echo first; echo second"],
            standardInput: null,
            TimeSpan.FromSeconds(30),
            progress,
            CancellationToken.None);

        Assert.Equal(["first", "second"], progress.Lines);
        Assert.Contains("first", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stderr_is_captured_separately_from_stdout()
    {
        if (!Posix)
        {
            return;
        }

        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "/bin/sh",
            ["-c", "echo out; echo err >&2; exit 3"],
            standardInput: null,
            TimeSpan.FromSeconds(30),
            standardOutputLines: null,
            CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Equal("err", result.StandardError.Trim());
    }

    [Fact]
    public async Task A_child_that_outlives_the_timeout_is_killed_and_reported_as_timed_out()
    {
        if (!Posix)
        {
            return;
        }

        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            "/bin/sleep",
            ["30"],
            standardInput: null,
            TimeSpan.FromMilliseconds(300),
            standardOutputLines: null,
            CancellationToken.None);

        // A hung powershell.exe or wsl.exe must not pin a job forever.
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task The_callers_cancellation_is_a_cancellation_and_not_a_timeout()
    {
        if (!Posix)
        {
            return;
        }

        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            "/bin/sleep",
            ["30"],
            standardInput: null,
            TimeSpan.FromMinutes(5),
            standardOutputLines: null,
            cancellation.Token));
    }
}
