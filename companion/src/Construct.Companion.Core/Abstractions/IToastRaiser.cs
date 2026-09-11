namespace Construct.Companion.Core.Abstractions;

// Raises a prepared toast document under the shared Construct AUMID.
// Reports registration and mute availability without spawning PowerShell.
public interface IToastRaiser
{
    Task<ToastAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task RaiseAsync(ToastDocument toast, CancellationToken cancellationToken = default);
}
public enum ToastAvailability { Available, Muted, Unregistered }
public sealed record ToastDocument(string Xml);
