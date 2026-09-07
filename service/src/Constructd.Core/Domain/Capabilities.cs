namespace Constructd.Core.Domain;

public sealed record ConsoleCapabilities(
    CapabilityLevel Screenshot,
    CapabilityLevel Keyboard,
    CapabilityLevel MouseAbsolute,
    CapabilityLevel MouseRelative,
    CapabilityLevel Interactive,
    int MaxScreenshotBytes,
    bool NativeResolutionOnly);

public sealed record NetworkCapabilities(
    CapabilityLevel ClientForward,
    CapabilityLevel HostForwardPrimary,
    CapabilityLevel HostForwardChild,
    CapabilityLevel DirectAddressReporting,
    CapabilityLevel AddressVerification,
    CapabilityLevel Isolation);

/// <summary>Per-VM network view (child: HostForward = HostForwardChild lowered by policy; primary: HostForwardPrimary).</summary>
public sealed record VmNetworkCapabilities(CapabilityLevel ClientForward, CapabilityLevel HostForward, CapabilityLevel AddressVerification);

/// <summary>Superset of the existing DriverCapabilities; the legacy values are embedded unchanged.</summary>
public sealed record BackendCapabilities(
    string Backend,
    Constructd.Core.Abstractions.DriverCapabilities Legacy,
    IReadOnlyList<int> Generations,
    int DefaultGeneration,
    CapabilityLevel SecureBoot,
    IReadOnlyList<SecureBootTemplate> SecureBootTemplates,
    CapabilityLevel Tpm,
    bool SecureBootTemplateLockedAfterTpmInit,
    int MaxOpticalDrives,
    CapabilityLevel AuxiliaryMedia,
    CapabilityLevel BootOrder,
    ConsoleCapabilities Console,
    NetworkCapabilities Network,
    CapabilityLevel DynamicMemory,
    CapabilityLevel MemoryOvercommit,
    CapabilityLevel Suspend,
    CapabilityLevel GracefulShutdown,
    IReadOnlyList<string> Notes);

/// <summary>What ONE VM can do right now (§3.3, per-VM runtime).</summary>
public sealed record VmCapabilitiesSnapshot(
    string VmName,
    VmState State,
    bool VideoHeadPresent,
    bool KeyboardPresent,
    bool SyntheticMousePresent,
    bool Ps2MousePresent,
    int? NativeWidth,
    int? NativeHeight,
    bool SecureBootTemplateLocked,
    int Generation,
    CapabilityLevel GracefulShutdown,
    VmNetworkCapabilities Network);
