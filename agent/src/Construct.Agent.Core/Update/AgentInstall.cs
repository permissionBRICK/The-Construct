using System.Text.Json;

namespace Construct.Agent.Update;

/// <param name="Current">The version in <c>app/</c> (on trial while <see cref="Pending"/>).</param>
/// <param name="Previous">The version before it, kept for the way back only while <see cref="Current"/> is on trial (12.2).</param>
/// <param name="Pending">True from the switch until <see cref="Current"/> passed its self-test once.</param>
/// <param name="Failed">A version whose move into <c>app/</c> failed: not installed automatically again (a manual install still is).</param>
public sealed record InstallState(string? Current, string? Previous, bool Pending = false, string? Failed = null);

/// <summary>
/// The agent's installed package in its directory. The agent always runs from <c>app/</c>, one path for every version,
/// so whatever is bound to the path (the Run value or the systemd unit, the <c>construct://</c> handler, the place
/// Windows keeps for the tray icon, an antivirus exclusion) stays across updates. An update is unpacked into
/// <c>next/</c> and started there; a copy started anywhere in the directory but <c>app/</c> never runs as the agent:
/// it moves itself into <c>app/</c> and starts the agent there (<see cref="InstallIntoApp"/>). While the new version
/// is on trial, the one it replaced waits in <c>previous/</c> for the way back; once the new one passed its self-test,
/// <see cref="Clean"/> leaves only <c>app/</c>. <c>install.json</c> names the current and the previous version
/// (12.1, 12.2). Installs made before <c>app/</c> ran from <c>versions/&lt;version&gt;/</c>: the first version
/// started from there moves into <c>app/</c> the same way, and the folder goes once that version is confirmed.
/// </summary>
public sealed class AgentInstall(string dataDirectory, string executableName)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Where a running agent was started from, as far as the install is concerned.</summary>
    public enum Place
    {
        /// <summary><c>app/</c>: the installed agent.</summary>
        App,

        /// <summary>Elsewhere in the agent's directory (<c>next/</c>, <c>previous/</c>, an old <c>versions/&lt;v&gt;/</c>): a copy to move into <c>app/</c>.</summary>
        Staged,

        /// <summary>Outside the agent's directory: a build run by hand, which neither installs nor updates itself.</summary>
        Elsewhere,
    }

    public string AppDirectory => Path.Combine(dataDirectory, "app");

    /// <summary>The update being installed: unpacked here, started from here.</summary>
    public string NextDirectory => Path.Combine(dataDirectory, "next");

    /// <summary>The version an update replaced, while the new one is on trial.</summary>
    public string PreviousDirectory => Path.Combine(dataDirectory, "previous");

    /// <summary>Installs made before <c>app/</c>: one directory per version.</summary>
    public string LegacyVersionsDirectory => Path.Combine(dataDirectory, "versions");

    private string IncomingDirectory => Path.Combine(dataDirectory, "app.incoming");

    private string FailedDirectory => Path.Combine(dataDirectory, "app.failed");

    public string StatePath => Path.Combine(dataDirectory, "install.json");

    public string ExecutableName => executableName;

    public string AppExecutable => Path.Combine(AppDirectory, executableName);

    public string NextExecutable => Path.Combine(NextDirectory, executableName);

    /// <summary>The file in a package directory that names its version (written when the package is unpacked).</summary>
    public const string VersionFile = ".version";

    public InstallState Read()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllBytes(StatePath), Json) ?? new(null, null) : new(null, null); }
        catch (JsonException) { return new(null, null); }
    }

    public void Write(InstallState state) => PrivateFiles.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(state, Json));

    public Place PlaceOf(string? processPath)
    {
        if (processPath is null) return Place.Elsewhere;
        var full = Path.GetFullPath(processPath);
        if (Within(full, AppDirectory)) return Place.App;
        return Within(full, dataDirectory) ? Place.Staged : Place.Elsewhere;
    }

    /// <summary>The installed version this process runs as: its own version when it runs from <c>app/</c>, else null.</summary>
    public string? RunningVersion(string? processPath, string ownVersion) => PlaceOf(processPath) == Place.App ? ownVersion : null;

    /// <summary>The version unpacked in <c>next/</c>, or null.</summary>
    public string? StagedVersion() => VersionIn(NextDirectory);

    /// <summary>
    /// The executable the way back starts: the previous version in <c>previous/</c>, or, for the first version after an
    /// install made before <c>app/</c>, the previous version's own <c>versions/&lt;version&gt;/</c>, which starts as it always did.
    /// </summary>
    public string? PreviousExecutable(InstallState state)
    {
        var moved = Path.Combine(PreviousDirectory, executableName);
        if (File.Exists(moved)) return moved;
        if (state.Previous is null) return null;
        var legacy = Path.Combine(LegacyVersionsDirectory, DirectoryName(state.Previous), executableName);
        return File.Exists(legacy) ? legacy : null;
    }

    /// <summary>
    /// Unpacks a package into <c>next/</c> (through <c>next.partial/</c>, so <c>next/</c> is either the whole package or absent)
    /// and records its version there.
    /// </summary>
    public void Stage(Action<string> unpack, string version)
    {
        var partial = NextDirectory + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        unpack(partial);
        if (!File.Exists(Path.Combine(partial, executableName))) throw new InvalidDataException($"The package has no {executableName}.");
        File.WriteAllText(Path.Combine(partial, VersionFile), version);
        if (Directory.Exists(NextDirectory)) Directory.Delete(NextDirectory, recursive: true);
        Directory.Move(partial, NextDirectory);
    }

    /// <summary>
    /// Puts the copy in <paramref name="source"/> into <c>app/</c>; the agent that ran from <c>app/</c> has stopped. Started from
    /// <c>next/</c> (an update) or an old <c>versions/</c> folder, what <c>app/</c> held becomes <c>previous/</c> for the way back;
    /// started from <c>previous/</c> (the way back), what <c>app/</c> held failed its self-test and is dropped. The copy goes
    /// to <c>app.incoming/</c> first and <c>app/</c> is swapped by two renames; when the second fails, <c>app/</c> is put back.
    /// </summary>
    public void InstallIntoApp(string source, TimeSpan? retryFor = null)
    {
        source = Path.GetFullPath(source);
        if (Same(source, AppDirectory)) return;
        var keepForTheWayBack = !Same(source, PreviousDirectory);
        if (Directory.Exists(IncomingDirectory)) Directory.Delete(IncomingDirectory, recursive: true);
        CopyTree(source, IncomingDirectory);
        string? aside = null;
        if (Directory.Exists(AppDirectory))
        {
            aside = keepForTheWayBack ? PreviousDirectory : FailedDirectory;
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            // The agent that ran from app/ has just exited: a scanner or a WebView2 process may hold a file for a moment.
            Retry(() => Directory.Move(AppDirectory, aside), retryFor ?? TimeSpan.FromSeconds(15));
        }

        try
        {
            Directory.Move(IncomingDirectory, AppDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (aside is not null && !Directory.Exists(AppDirectory)) Directory.Move(aside, AppDirectory);
            throw;
        }
    }

    /// <summary>
    /// Removes what only an update in progress needs, once the version in <c>app/</c> is confirmed: <c>next/</c>,
    /// <c>previous/</c>, the dropped and incoming copies and the <c>versions/</c> folder of installs made before <c>app/</c>.
    /// A file still in use (the copy that installed this one may still be exiting) stays for the next start.
    /// </summary>
    /// <returns>True when nothing is left.</returns>
    public bool Clean()
    {
        var clean = true;
        foreach (var directory in new[] { NextDirectory, NextDirectory + ".partial", PreviousDirectory, IncomingDirectory, FailedDirectory, LegacyVersionsDirectory })
        {
            if (!Directory.Exists(directory)) continue;
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { clean = false; }
        }

        return clean;
    }

    /// <summary>A version as a directory name: <c>X.Y.Z+sha</c> is kept, anything else reduced to safe characters.</summary>
    public static string DirectoryName(string version) =>
        new(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-' or '_' ? c : '_').ToArray());

    private static string? VersionIn(string directory)
    {
        var file = Path.Combine(directory, VersionFile);
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() is { Length: > 0 } version ? version : null : null; }
        catch (IOException) { return null; }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, copy, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy, File.GetUnixFileMode(file));
        }
    }

    private static void Retry(Action action, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < until)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static bool Within(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
