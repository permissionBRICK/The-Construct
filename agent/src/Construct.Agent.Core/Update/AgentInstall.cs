using System.Text.Json;

namespace Construct.Agent.Update;

/// <param name="Current">The version that starts (a directory under <c>versions/</c>).</param>
/// <param name="Previous">The version before it, kept for the way back (12.2).</param>
/// <param name="Pending">True from the switch until <see cref="Current"/> passed its self-test once.</param>
public sealed record InstallState(string? Current, string? Previous, bool Pending = false);

/// <summary>
/// The agent's installed packages in its directory: <c>versions/&lt;version&gt;/</c>, one per package,
/// and <c>install.json</c> naming the current and the previous one. The install scripts unpack the first
/// package here; the updater adds the next and switches (12.1, 12.2).
/// </summary>
public sealed class AgentInstall(string dataDirectory, string executableName)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string VersionsDirectory => Path.Combine(dataDirectory, "versions");

    public string StatePath => Path.Combine(dataDirectory, "install.json");

    public string ExecutableName => executableName;

    public InstallState Read()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllBytes(StatePath), Json) ?? new(null, null) : new(null, null); }
        catch (JsonException) { return new(null, null); }
    }

    public void Write(InstallState state) => PrivateFiles.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, Json));

    public string DirectoryOf(string version) => Path.Combine(VersionsDirectory, DirectoryName(version));

    public string ExecutableOf(string version) => Path.Combine(DirectoryOf(version), executableName);

    /// <summary>The installed version this process runs from, or null for a build run from elsewhere.</summary>
    public string? RunningVersion(string? processPath)
    {
        if (processPath is null) return null;
        var versions = Path.GetFullPath(VersionsDirectory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(processPath);
        if (!full.StartsWith(versions, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        var name = full[versions.Length..].Split(Path.DirectorySeparatorChar)[0];
        var state = Read();
        return new[] { state.Current, state.Previous }.FirstOrDefault(v => v is not null && DirectoryName(v) == name) ?? name;
    }

    /// <summary>Removes every version but the current and the previous one.</summary>
    public void Prune()
    {
        var state = Read();
        if (!Directory.Exists(VersionsDirectory)) return;
        var keep = new[] { state.Current, state.Previous }.Where(v => v is not null).Select(v => DirectoryName(v!)).ToHashSet(StringComparer.Ordinal);
        foreach (var directory in Directory.GetDirectories(VersionsDirectory))
        {
            if (keep.Contains(Path.GetFileName(directory))) continue;
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a file still in use: the next prune */ }
        }
    }

    /// <summary>A version as a directory name: <c>X.Y.Z+sha</c> is kept, anything else reduced to safe characters.</summary>
    public static string DirectoryName(string version) =>
        new(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-' or '_' ? c : '_').ToArray());
}
