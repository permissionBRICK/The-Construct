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
/// Windows 11 remembers per executable path whether a tray icon shows on the taskbar, and each update runs the agent from
/// a new <c>versions/&lt;version&gt;/</c> directory: without help every update put the icon back into the overflow
/// (FatPC 2026-11-22). Once Windows has an entry for this version's icon, the choice made for the previous version is
/// copied to it (when there is none, a taskbar place any earlier version or the old Companion had), unless the user
/// already chose for this version; entries of versions no longer on disk are removed. A fixed icon GUID (<c>NIF_GUID</c>)
/// would be the documented way, but Windows binds such a GUID to one path unless every build is signed by one publisher.
/// </summary>
public static class TrayIconCarryOver
{
    public const string CompanionExecutable = "ConstructCompanion.exe";

    public enum Outcome
    {
        /// <summary>Windows has no entry for this executable yet: ask again in a moment.</summary>
        NotYet,
        Unchanged,
        /// <summary>This version's entry got the earlier choice: the icon is added again so the taskbar reads it.</summary>
        Changed,
    }

    /// <param name="self">This process's executable.</param>
    /// <param name="versionsDirectory">The agent's <c>versions/</c> directory.</param>
    /// <param name="previous">The previous version's executable (<c>install.json</c>), when there is one.</param>
    /// <param name="exists">Whether a file exists (the tests fake the disk).</param>
    public static Outcome Apply(ITrayIconSettings settings, string self, string versionsDirectory, string? previous, Func<string, bool> exists)
    {
        var entries = settings.List();
        var own = entries.FirstOrDefault(e => Same(e.ExecutablePath, self));
        if (own is null) return Outcome.NotYet;

        var root = versionsDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var older = entries.Where(e => e != own && e.ExecutablePath is { } path && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var gone in older.Where(e => !exists(e.ExecutablePath!)))
        {
            try { settings.Delete(gone.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        if (own.Promoted is not null) return Outcome.Unchanged;
        var choice = previous is null ? null : older.FirstOrDefault(e => Same(e.ExecutablePath, previous))?.Promoted;
        choice ??= older.Concat(entries.Where(e => string.Equals(Path.GetFileName(e.ExecutablePath?.Replace('\\', '/')), CompanionExecutable, StringComparison.OrdinalIgnoreCase)))
            .Any(e => e.Promoted == 1) ? 1 : null;
        if (choice is not { } value) return Outcome.Unchanged;
        settings.SetPromoted(own.Id, value);
        return Outcome.Changed;
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
