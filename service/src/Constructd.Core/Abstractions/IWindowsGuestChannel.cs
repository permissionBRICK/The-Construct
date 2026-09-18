using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

public sealed record WindowsGuestObservation(double? Uptime, WindowsGuestReport? Report);
public interface IWindowsGuestChannel
{
    Task<WindowsGuestObservation> ObserveWindowsAsync(string name, string incarnation, CancellationToken ct);
    Task DeliverWindowsKeyAsync(string name, string incarnation, string key, CancellationToken ct);
    Task ClearWindowsKeyAsync(string name, string incarnation, CancellationToken ct);
    /// <summary>Ejects live media, checking immutable VM identity at the mutation boundary.</summary>
    Task EjectWindowsMediaAsync(string name, string incarnation, bool installOnly, CancellationToken ct);
}
