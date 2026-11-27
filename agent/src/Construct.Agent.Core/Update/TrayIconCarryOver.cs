namespace Construct.Agent.Update;

/// <summary>One notification-area icon as Windows 11 remembers it (<c>HKCU\Control Panel\NotifyIconSettings\&lt;id&gt;</c>).</summary>
/// <param name="Promoted"><c>IsPromoted</c>: 1 shown on the taskbar, 0 kept in the overflow, null never chosen.</param>
public sealed record TrayIconEntry(string Id, string? ExecutablePath, int? Promoted);

/// <summary>The notification-area icons Windows remembers for the user.</summary>
public interface ITrayIconSettings
{
    IReadOnlyList<TrayIconEntry> List();

    void SetPromoted(string id, int value);

    void Delete(string id);
}

/// <summary>
/// The tray icon's first place. Windows 11 remembers per executable path whether a tray icon shows on the taskbar, and the
/// agent always runs from <c>app\</c>, so after the first start the place stays wherever the user puts it, across updates.
/// That first start (the caller runs this once, then writes <see cref="DoneFile"/>) gives the icon a place the user has not
/// chosen yet: the one an earlier install had (an agent from the old <c>versions\&lt;v&gt;\</c> folders, or the old
/// Companion), else the taskbar; the entries of those earlier agent paths are removed. A fixed icon GUID (<c>NIF_GUID</c>)
/// would be the documented way, but Windows binds such a GUID to one path unless every build is signed by one publisher.
/// </summary>
public static class TrayIconCarryOver
{
    public const string CompanionExecutable = "ConstructCompanion.exe";

    /// <summary>The file in the agent's directory that says the first place was given.</summary>
    public const string DoneFile = "tray-placed";

    public enum Outcome
    {
        /// <summary>Windows has no entry for this executable yet: ask again in a moment.</summary>
        NotYet,
        Unchanged,
        /// <summary>The entry got its first place: the icon is added again so the taskbar reads it.</summary>
        Changed,
    }

    /// <param name="self">This process's executable (in <c>app\</c>).</param>
    /// <param name="agentDirectory">The agent's directory: entries of other executables in it are earlier installs.</param>
    /// <param name="before">The executable of the version that ran before this one (an old <c>versions\&lt;v&gt;\</c>), whose choice comes first.</param>
    public static Outcome Apply(ITrayIconSettings settings, string self, string agentDirectory, string? before = null)
    {
        var entries = settings.List();
        var own = entries.FirstOrDefault(e => Same(e.ExecutablePath, self));
        if (own is null) return Outcome.NotYet;

        var root = agentDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var older = entries.Where(e => e != own && e.ExecutablePath is { } path && path.Replace('/', '\\').StartsWith(root.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var gone in older)
        {
            try { settings.Delete(gone.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        if (own.Promoted is not null) return Outcome.Unchanged;
        var companion = entries.Where(e => string.Equals(Path.GetFileName(e.ExecutablePath?.Replace('\\', '/')), CompanionExecutable, StringComparison.OrdinalIgnoreCase));
        var earlier = older.Concat(companion).Select(e => e.Promoted).Where(p => p is not null).ToArray();
        // The version that ran before decides; else any earlier taskbar place, else an earlier overflow place; a fresh
        // install goes on the taskbar.
        var value = (before is null ? null : older.FirstOrDefault(e => Same(e.ExecutablePath, before))?.Promoted)
            ?? (earlier.Length == 0 || earlier.Contains(1) ? 1 : 0);
        settings.SetPromoted(own.Id, value);
        return Outcome.Changed;
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
