namespace Construct.Agent.Upgrade;

/// <summary>
/// After the move (R14), the old install folder's entry scripts are the new Construct's, so a T3 Code Desktop or panel of
/// the old Construct can only act through the agent, and never runs the old client's reprovision against a moved VM. The
/// shims ship with the agent, as an update through the old updater would have left them (<c>Update-T3Code.ps1</c>,
/// <c>Get-ConstructT3PairingLink.ps1</c>, <c>Update-Construct.ps1</c>, <c>install.ps1</c>,
/// <c>lib\Construct.AgentShim.ps1</c>); every other script at the folder's top (<c>Provision-AgentVM.ps1</c>,
/// <c>Create-AgentVM.ps1</c>, ...) becomes <see cref="MovedScript"/>, which only says where things moved. Only the old client's own install folders
/// (<see cref="LegacyState.OwnInstallFolders"/>), and only regular files there: a link or junction on the way is refused,
/// so no write lands outside. A script that could not be replaced is kept in <c>bridge.json</c> with a note naming it
/// (<see cref="Protect"/>) and tried again. The way back (13.2) reinstalls the last old release, which brings its
/// scripts back.
/// </summary>
public static class OldInstallScripts
{
    /// <summary>The scripts that take the old ones' place, by name; their text ships inside the agent.</summary>
    public static readonly IReadOnlyList<string> Shims = ["install.ps1", "Update-Construct.ps1", "Update-T3Code.ps1", "Get-ConstructT3PairingLink.ps1"];

    public const string ShimLibrary = "lib/Construct.AgentShim.ps1";

    /// <summary>What every other old entry script becomes (<c>lib/Construct.MovedScript.ps1</c>): it says where things moved and does nothing.</summary>
    public const string MovedScript = "lib/Construct.MovedScript.ps1";

    /// <summary>The start of the note that names the old scripts not replaced.</summary>
    public const string LeftNote = "Old scripts not replaced, do not run them: ";

    private static readonly object Gate = new();

    /// <summary>A shim's text as the agent carries it.</summary>
    public static byte[] Shim(string name)
    {
        var resource = "Shims." + name.Replace('/', '.');
        using var stream = typeof(OldInstallScripts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The agent carries no " + name + ".");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// <see cref="Replace"/>, with what it left kept in <c>bridge.json</c> and named in one note (gone once none is left);
    /// true when none is.
    /// </summary>
    public static bool Protect(LegacyState legacy, BridgeFile bridge, string? account)
    {
        var left = Replace(legacy);
        bridge.Update(b => (b with { ScriptsLeft = left }).Noted(account, LeftNote, left.Count == 0 ? null : LeftNote + string.Join(", ", left)));
        return left.Count == 0;
    }

    /// <summary>Puts the shims in place in every own install folder; the scripts left as they were (a file Windows holds, a link).</summary>
    public static IReadOnlyList<string> Replace(LegacyState legacy)
    {
        lock (Gate)
        {
            var left = new List<string>();
            var moved = Shim(MovedScript);
            foreach (var folder in legacy.OwnInstallFolders())
            {
                foreach (var name in Shims) Write(legacy.Root, Path.Combine(folder, name), Shim(name), left);
                Write(legacy.Root, Path.Combine(folder, "lib", "Construct.AgentShim.ps1"), Shim(ShimLibrary), left);
                foreach (var script in Directory.EnumerateFiles(folder, "*.ps1", SearchOption.TopDirectoryOnly))
                    if (!Shims.Contains(Path.GetFileName(script), StringComparer.OrdinalIgnoreCase)) Write(legacy.Root, script, moved, left);
            }

            return left;
        }
    }

    /// <summary>
    /// Writes the regular file in place unless it holds these bytes already, or creates it; a link or junction from
    /// <paramref name="root"/> down to it, the file included, is refused. Anything not written goes to <paramref name="left"/>.
    /// </summary>
    private static void Write(string root, string path, byte[] content, List<string> left)
    {
        try
        {
            if (Linked(root, path))
            {
                left.Add(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // CreateNew fails where anything is in the way, a dangling link included.
            using var file = new FileStream(path, File.Exists(path) ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var current = new byte[file.Length];
            file.ReadExactly(current);
            if (current.AsSpan().SequenceEqual(content)) return;
            file.SetLength(0);
            file.Write(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left.Add(path);
        }
    }

    /// <summary>Whether <paramref name="path"/> or a folder on the way to it from <paramref name="root"/> (that included) is a link, junction or other reparse point.</summary>
    private static bool Linked(string root, string path)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var at = Path.GetFullPath(path); at.Length >= top.Length; at = Path.GetDirectoryName(at)!)
        {
            FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
            if (info.LinkTarget is not null || info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            if (at.Length == top.Length) break;
        }

        return false;
    }
}
