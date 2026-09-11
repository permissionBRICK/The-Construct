using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Desktop;

// The installer (lib/Construct.Companion.ps1) writes the protocol and toast keys; the app only
// reads them and toggles its own Run value.
public sealed class DesktopRegistration(IRegistry registry, string exe)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ProtocolKey = @"Software\Classes\construct";
    public const string Aumid = "PermissionBrick.TheConstruct";
    public const string ToastKey = @"Software\Classes\AppUserModelId\PermissionBrick.TheConstruct";
    public bool Autostart => registry.ReadString(RunKey, "ConstructCompanion") == $"\"{exe}\" --background";
    public bool ToastRegistered => !string.IsNullOrEmpty(registry.ReadString(ToastKey, "DisplayName")) && !string.IsNullOrEmpty(registry.ReadString(ToastKey, "IconUri"));
    public void SetAutostart(bool enabled)
    {
        if (enabled) registry.WriteString(RunKey, "ConstructCompanion", $"\"{exe}\" --background");
        else registry.DeleteValue(RunKey, "ConstructCompanion");
    }
}
