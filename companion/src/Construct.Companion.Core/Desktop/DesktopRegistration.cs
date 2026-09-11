using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Desktop;

public sealed class DesktopRegistration(IRegistry registry, string exe, string icon)
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
    public void Register()
    {
        registry.WriteString(ProtocolKey, null, "URL:Construct Protocol"); registry.WriteString(ProtocolKey, "URL Protocol", "");
        registry.WriteString(ProtocolKey + @"\shell\open\command", null, $"\"{exe}\" --uri \"%1\"");
        registry.WriteString(ToastKey, "DisplayName", "The Construct"); registry.WriteString(ToastKey, "IconUri", icon);
    }
}
