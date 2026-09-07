using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public sealed record ChildVmDescriptor(
    string Name,
    ChildHardware Hardware,
    string? VhdPath,
    string? InstallMediaPath,
    string? AuxiliaryMediaPath,
    string SwitchName);

public sealed record AttachedMedia(string? InstallPath, string? AuxiliaryPath, bool Complete);

public interface IChildVmDriver
{
    Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct);
    /// <summary>Removes VM, disk chain and saved state. Missing VM is not an error. May turn a running VM off: deletion is destructive by request (§8.8).</summary>
    Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct);
    /// <summary>VM must be Off. resendTemplate=false never re-sends the Secure Boot template (locked after TPM init).</summary>
    Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct);
    Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct);
    /// <summary>What the hypervisor REALLY has attached (paths per slot), so references are reconciled to reality after a partial failure.</summary>
    Task<AttachedMedia> GetAttachedMediaAsync(string name, CancellationToken ct);
    /// <summary>Guest shutdown only (WMI InitiateShutdown, force=false) then poll until Off or timeout. Never -Force, -TurnOff, save or delete.</summary>
    Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct);
    Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct);
    /// <summary>Immutable hypervisor id of the VM (Hyper-V VM GUID) — the child's incarnation.</summary>
    Task<string?> GetVmIdAsync(string name, CancellationToken ct);
}

/// <summary>Read-only placement resolution before capacity admission, including Hyper-V defaults.</summary>
public sealed record ChildStoragePlacement(string DiskPath, string DiskVolume, string ConfigVolume);
public interface IChildVmStorage
{
    Task<ChildStoragePlacement> ResolveStorageAsync(string name, CancellationToken ct);
    Task<ChildStoragePlacement> ResolvePrimaryStorageAsync(string name, CancellationToken ct);
}

/// <summary>Creation ownership survives a partial driver failure; rollback must match the admitted job.</summary>
public interface IChildVmCreationOwnership
{
    Task CreateOwnedAsync(ChildVmDescriptor descriptor, string operationId, IProgress<string>? progress, CancellationToken ct);
    Task<string?> GetCreationOperationAsync(string name, CancellationToken ct);
}
