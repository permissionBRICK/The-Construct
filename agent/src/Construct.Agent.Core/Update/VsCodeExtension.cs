using System.Diagnostics;

namespace Construct.Agent.Update;

/// <summary>
/// The thin VS Code extension an agent package carries (<c>construct.vsix</c>). The install scripts put it
/// into VS Code at the first install; a version the agent switched to by itself does it here, once it has
/// passed its self-test, so the extension follows the agent (12.1). Best effort: the extension is a frame
/// around the agent's page, so an older one keeps working, and nothing here can fail an update.
/// </summary>
public static class VsCodeExtension
{
    public const string FileName = "construct.vsix";

    /// <summary>What happened: <c>installed</c>, <c>no-vscode</c>, <c>no-package</c> or <c>failed</c>.</summary>
    public static async Task<string> InstallAsync(string versionDirectory, string? searchPath = null, CancellationToken ct = default)
    {
        var vsix = Path.Combine(versionDirectory, FileName);
        if (!File.Exists(vsix)) return "no-package";
        var code = Find(searchPath);
        if (code is null) return "no-vscode";
        try
        {
            var start = new ProcessStartInfo(code)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            start.ArgumentList.Add("--install-extension");
            start.ArgumentList.Add(vsix);
            start.ArgumentList.Add("--force");
            using var process = Process.Start(start);
            if (process is null) return "failed";
            process.StandardInput.Close();
            // code writes warnings even when it succeeds: only its exit code counts.
            _ = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return "failed";
            }

            return process.ExitCode == 0 ? "installed" : "failed";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return "failed";
        }
    }

    /// <summary>VS Code's command line: on the search path, or in its two usual places on Windows.</summary>
    public static string? Find(string? searchPath = null)
    {
        var names = OperatingSystem.IsWindows() ? new[] { "code.cmd", "code.exe" } : ["code"];
        foreach (var directory in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim('"'), name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (!OperatingSystem.IsWindows() || searchPath is not null) return null;
        foreach (var (root, rest) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\bin\code.cmd"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\bin\code.cmd"),
        })
        {
            if (root.Length == 0) continue;
            var candidate = Path.Combine(root, rest);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
