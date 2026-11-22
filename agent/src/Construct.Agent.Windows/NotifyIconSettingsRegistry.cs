using System.Runtime.Versioning;
using Construct.Agent.Update;
using Microsoft.Win32;

namespace Construct.Agent.Windows;

/// <summary>Windows 11's remembered tray icons, <c>HKCU\Control Panel\NotifyIconSettings</c> (absent on Windows 10).</summary>
[SupportedOSPlatform("windows")]
internal sealed class NotifyIconSettingsRegistry : ITrayIconSettings
{
    private const string Key = @"Control Panel\NotifyIconSettings";

    public IReadOnlyList<TrayIconEntry> List()
    {
        using var root = Registry.CurrentUser.OpenSubKey(Key);
        if (root is null) return [];
        var list = new List<TrayIconEntry>();
        foreach (var id in root.GetSubKeyNames())
        {
            using var entry = root.OpenSubKey(id);
            if (entry is null) continue;
            list.Add(new(id, entry.GetValue("ExecutablePath") as string, entry.GetValue("IsPromoted") as int?));
        }

        return list;
    }

    public void SetPromoted(string id, int value)
    {
        using var entry = Registry.CurrentUser.OpenSubKey(Key + @"\" + id, writable: true);
        entry?.SetValue("IsPromoted", value, RegistryValueKind.DWord);
    }

    public void Delete(string id)
    {
        using var root = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        root?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
    }
}
