using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

public sealed class CapabilityAggregator(IHypervisorDriver legacy, IChildVmDriver child, IConsoleTransport console) : ICapabilityAggregator
{
    public async Task<BackendCapabilities> GetAsync(CancellationToken ct) =>
        (await child.GetCapabilitiesAsync(ct)) with { Legacy = legacy.Capabilities, Console = console.Capabilities };
}

public static class UnsupportedCapabilities
{
    public static ConsoleCapabilities Console { get; } = new(CapabilityLevel.Unsupported, CapabilityLevel.Unsupported,
        CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, 4 << 20, true);
    public static BackendCapabilities Backend(DriverCapabilities legacy) => new("hyperv", legacy, [], 2,
        CapabilityLevel.Unsupported, [], CapabilityLevel.Unsupported, false, 0, CapabilityLevel.Unsupported,
        CapabilityLevel.Unsupported, Console, new(CapabilityLevel.Supported, CapabilityLevel.Supported,
            CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported),
        CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported,
        ["Child VM, console, capacity and update backends have not been installed. Validated on Linux with fakes only."]);
}
