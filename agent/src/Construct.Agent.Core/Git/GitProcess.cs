using System.Diagnostics;

namespace Construct.Agent.Git;

/// <summary>
/// Runs this device's own git without any prompt: terminal prompts, askpass and the Git Credential Manager's dialogs are
/// off, nothing of this process's environment injects config or trace output, and every call has a time limit. The
/// device's credential helpers stay as configured (unless the arguments switch them off), so a call uses this PC's
/// stored credentials.
/// </summary>
public static class GitProcess
{
    /// <summary>The <c>git</c> executable on <paramref name="searchPath"/> (PATH syntax; null is this process's PATH), or null.</summary>
    public static string? Find(string? searchPath = null)
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        return null;
    }

    /// <summary>Runs git; null when it could not start or ran out of time (it is killed then). Output and error are returned, never logged.</summary>
    public static async Task<(int Exit, string Output, string Error)?> RunAsync(string git, IEnumerable<string> arguments, string? input,
        IReadOnlyDictionary<string, string>? secrets, TimeSpan timeout, CancellationToken ct, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(git)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Nothing from this process may inject config, trace output or a prompt into the child.
        foreach (var key in start.Environment.Keys.ToArray())
        {
            if (key.StartsWith("GIT_TRACE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CONSTRUCT_GIT_", StringComparison.OrdinalIgnoreCase)
                || key is "GIT_ASKPASS" or "SSH_ASKPASS" or "SSH_ASKPASS_REQUIRE" or "GIT_CONFIG_PARAMETERS" or "GIT_DIR" or "GIT_WORK_TREE")
                start.Environment.Remove(key);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_ASKPASS"] = "";
        foreach (var (key, value) in secrets ?? new Dictionary<string, string>()) start.Environment[key] = value;

        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(limit.Token);
                var error = process.StandardError.ReadToEndAsync(limit.Token);
                try
                {
                    if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException) { /* git ended without reading its input */ }

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }
    }
}
