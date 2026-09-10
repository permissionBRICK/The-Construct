namespace Constructd.Tests.Support;

/// <summary>Linux client integration must not break the portable service test command.</summary>
public sealed class LinuxToolchainFactAttribute : FactAttribute
{
    public LinuxToolchainFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "The guest CLI end-to-end story requires Linux.";
            return;
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var missing = new[] { "bash", "node", "curl", "jq", "python3" }
            .Where(tool => !directories.Any(directory => File.Exists(Path.Combine(directory, tool)))).ToArray();
        if (missing.Length > 0)
            Skip = "The guest CLI end-to-end story requires tools on PATH: " + string.Join(", ", missing) + ".";
    }
}
