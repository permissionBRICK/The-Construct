using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

/// <summary>Fail-closed production seam until the child backend is installed.</summary>
public sealed class UnsupportedChildVmDriver(IHypervisorDriver legacy) : IChildVmDriver
{
    public Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(UnsupportedCapabilities.Backend(legacy.Capabilities) with { Backend = legacy.GetType().Name.StartsWith("Fake", StringComparison.Ordinal) ? "fake" : "hyperv" });
    public Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct) => throw Unsupported();
    public Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct) => throw Unsupported();
    public Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct) => throw Unsupported();
    public Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct) => throw Unsupported();
    public Task<AttachedMedia> GetAttachedMediaAsync(string name, CancellationToken ct) => throw Unsupported();
    public Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct) => Task.FromResult(GracefulShutdownOutcome.Unavailable);
    public Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct) => throw Unsupported();
    public Task<string?> GetVmIdAsync(string name, CancellationToken ct) => throw Unsupported();
    private static NotSupportedException Unsupported() => new("Child VM backend is unsupported.");
}
