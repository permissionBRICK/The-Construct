using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeToastRaiser : IToastRaiser
{
    public ToastAvailability Availability { get; set; } = ToastAvailability.Available;
    public List<ToastDocument> Toasts { get; } = [];
    public Task<ToastAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Availability); }
    public Task RaiseAsync(ToastDocument toast, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Toasts.Add(toast); return Task.CompletedTask; }
}
