using System.Runtime.Versioning;
using Microsoft.Win32;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class CurrentUserRegistry : IRegistry
{
    public string? ReadString(string key, string? name) { using var opened = Registry.CurrentUser.OpenSubKey(key); return opened?.GetValue(name ?? "") as string; }
    public void WriteString(string key, string? name, string value) { using var opened = Registry.CurrentUser.CreateSubKey(key); opened.SetValue(name ?? "", value, RegistryValueKind.String); }
    public void DeleteValue(string key, string? name) { using var opened = Registry.CurrentUser.OpenSubKey(key, true); opened?.DeleteValue(name ?? "", false); }
    public void DeleteTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, false);
}
