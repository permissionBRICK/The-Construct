using System.Runtime.Versioning;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WinRtToastRaiser(IRegistry registry) : IToastRaiser
{
    public Task<ToastAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (registry.ReadString(DesktopRegistration.ToastKey, "DisplayName") is null || registry.ReadString(DesktopRegistration.ToastKey, "IconUri") is null)
            return Task.FromResult(ToastAvailability.Unregistered);
        var notifier = ToastNotificationManager.CreateToastNotifier(DesktopRegistration.Aumid);
        return Task.FromResult(notifier.Setting == NotificationSetting.Enabled ? ToastAvailability.Available : ToastAvailability.Muted);
    }
    public async Task RaiseAsync(ToastDocument toast, CancellationToken cancellationToken = default)
    {
        if (await GetAvailabilityAsync(cancellationToken) != ToastAvailability.Available) return;
        var xml = new XmlDocument(); xml.LoadXml(toast.Xml);
        ToastNotificationManager.CreateToastNotifier(DesktopRegistration.Aumid).Show(new ToastNotification(xml));
    }
}
