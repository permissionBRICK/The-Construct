using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;

namespace Constructd.Proxmox;

/// <summary>
/// Child VMs are not offered on Proxmox yet: this answers the composition's three child-VM seams
/// (<see cref="IChildVmDriver"/>, <see cref="IChildVmStorage"/>, <see cref="IChildVmCreationOwnership"/>)
/// with the Core's unsupported behaviour, so the routes refuse cleanly with
/// <c>unsupported-capability</c> and the primary-VM paths that ask for a storage placement get one.
/// </summary>
public sealed class ProxmoxChildVmPlatform(IHypervisorDriver driver, ConstructdOptions options)
    : IChildVmDriver, IChildVmStorage, IChildVmCreationOwnership
{
    private readonly UnsupportedChildVmDriver _inner = new(driver);

    public async Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        (await _inner.GetCapabilitiesAsync(ct).ConfigureAwait(false)) with { Backend = "proxmox" };

    public Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct) =>
        _inner.CreateAsync(descriptor, progress, ct);

    public Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct) =>
        _inner.RemoveAsync(name, progress, ct);

    public Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct) =>
        _inner.UpdateHardwareAsync(name, hardware, resendTemplate, ct);

    public Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct) =>
        _inner.SetMediaAsync(name, installMediaPath, auxiliaryMediaPath, bootOrder, ct);

    public Task<AttachedMedia> GetAttachedMediaAsync(string name, CancellationToken ct) =>
        _inner.GetAttachedMediaAsync(name, ct);

    public Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct) =>
        _inner.ShutdownGracefulAsync(name, timeout, progress, ct);

    public Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct) =>
        _inner.GetVmCapabilitiesAsync(name, ct);

    public Task<string?> GetVmIdAsync(string name, CancellationToken ct) => _inner.GetVmIdAsync(name, ct);

    /// <summary>
    /// Every VM's disk — and its suspend-to-disk state — lives on the configured storage; the
    /// placement names it. The disk path is keyed by VM NAME because Proxmox assigns the numeric id
    /// only at creation; <see cref="ProxmoxInventory"/> answers evidence for that path by name.
    /// </summary>
    public Task<ChildStoragePlacement> ResolvePrimaryStorageAsync(string name, CancellationToken ct) =>
        ResolveStorageAsync(name, ct);

    public Task<ChildStoragePlacement> ResolveStorageAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var storage = options.Proxmox.Storage;
        return Task.FromResult(new ChildStoragePlacement($"{storage}:vm-{name}-disk-0", storage, storage));
    }

    public Task CreateOwnedAsync(ChildVmDescriptor descriptor, string operationId, IProgress<string>? progress, CancellationToken ct) =>
        throw new NotSupportedException("Child VMs are not available on a Proxmox host.");

    public Task<string?> GetCreationOperationAsync(string name, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
