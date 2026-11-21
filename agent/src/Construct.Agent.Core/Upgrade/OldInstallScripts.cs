namespace Construct.Agent.Upgrade;

/// <summary>
/// After the move (R14), the old install folder's entry scripts are the new Construct's, so a T3 Code Desktop or panel of
/// the old Construct can only act through the agent, and never runs the old client's reprovision against a moved VM. The
/// shims ship with the agent, as an update through the old updater would have left them (<c>Update-T3Code.ps1</c>,
/// <c>Get-ConstructT3PairingLink.ps1</c>, <c>Update-Construct.ps1</c>, <c>install.ps1</c>,
/// <c>lib\Construct.AgentShim.ps1</c>); every other script at the folder's top (<c>Provision-AgentVM.ps1</c>,
/// <c>Create-AgentVM.ps1</c>, ...) becomes <see cref="MovedScript"/>, which only says where things moved. Only the old client's own install folders
/// (<see cref="LegacyState.OwnInstallFolders"/>). The way back (13.2) reinstalls the last old release, which brings its
/// scripts back.
/// </summary>
public static class OldInstallScripts
{
    /// <summary>The scripts that take the old ones' place, by name; their text ships inside the agent.</summary>
    public static readonly IReadOnlyList<string> Shims = ["install.ps1", "Update-Construct.ps1", "Update-T3Code.ps1", "Get-ConstructT3PairingLink.ps1"];

    public const string ShimLibrary = "lib/Construct.AgentShim.ps1";

    /// <summary>What every other old entry script becomes (<c>lib/Construct.MovedScript.ps1</c>): it says where things moved and does nothing.</summary>
    public const string MovedScript = "lib/Construct.MovedScript.ps1";

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

    /// <summary>Puts the shims in place in every own install folder; true when nothing is left to replace (a file Windows holds stays for the next start).</summary>
    public static bool Replace(LegacyState legacy)
    {
        var complete = true;
        var moved = Shim(MovedScript);
        foreach (var folder in legacy.OwnInstallFolders())
        {
            foreach (var name in Shims) complete &= Write(Path.Combine(folder, name), Shim(name));
            complete &= Write(Path.Combine(folder, "lib", "Construct.AgentShim.ps1"), Shim(ShimLibrary));
            foreach (var script in Directory.EnumerateFiles(folder, "*.ps1", SearchOption.TopDirectoryOnly))
                if (!Shims.Contains(Path.GetFileName(script), StringComparer.OrdinalIgnoreCase)) complete &= Write(script, moved);
        }

        return complete;
    }

    /// <summary>Writes the file unless it holds these bytes already; false when Windows would not let it.</summary>
    private static bool Write(string path, byte[] content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
