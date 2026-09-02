using System.Diagnostics;

namespace Constructd.Tests.Windows;

/// <summary>
/// Runs <c>service/tests/host-installer.test.ps1</c> as part of <c>dotnet test</c>, so the installer's
/// parser check and parameter contract are covered by the same command as everything else rather than
/// by a script somebody has to remember.
///
/// The PowerShell test does the asserting; this only reports its outcome. Where <c>pwsh</c> is not
/// installed the test passes with a note — the file is still there to run by hand, and the alternative
/// (failing the whole suite on a machine without PowerShell) helps nobody.
/// </summary>
public sealed class HostInstallerTests
{
    [Fact]
    public void The_host_installer_scripts_parse_and_keep_their_parameter_contract()
    {
        var script = FindRepoFile("service/tests/host-installer.test.ps1");
        Assert.True(File.Exists(script), $"Installer test script not found at {script}.");

        var pwsh = FindPwsh();
        if (pwsh is null)
        {
            // Nothing to run it with; the script itself is the test and is unchanged.
            return;
        }

        var startInfo = new ProcessStartInfo(pwsh)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "The installer test script did not finish within two minutes.");

        Assert.True(process.ExitCode == 0, $"host-installer.test.ps1 failed:{Environment.NewLine}{output}");
        Assert.Contains("0 failed", output, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return relativePath;
    }

    private static string? FindPwsh()
    {
        foreach (var candidate in new[] { "/usr/local/bin/pwsh", "/usr/bin/pwsh" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var directory in paths)
        {
            foreach (var name in new[] { "pwsh", "pwsh.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
