using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed class FakeChildVmDriver(FakeHypervisorDriver hypervisor) : IChildVmDriver, IChildVmStorage, IChildVmCreationOwnership
{
    private readonly ConcurrentDictionary<string, string> _creationOperations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _templateLocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (ChildVmDescriptor Descriptor, string Id)> _vms = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> Calls { get; } = new();
    public Exception? Failure { get; set; }
    public Exception? RemoveFailure { get; set; }
    public Exception? FailureAfterCreate { get; set; }
    public GracefulShutdownOutcome ShutdownOutcome { get; set; } = GracefulShutdownOutcome.Completed;
    public BackendCapabilities Capabilities { get; set; } = UnsupportedCapabilities.Backend(new(false, true, DriverConsole.None)) with
    {
        Backend = "fake",
        Generations = [2],
        SecureBoot = CapabilityLevel.Supported,
        SecureBootTemplates = [SecureBootTemplate.MicrosoftWindows, SecureBootTemplate.MicrosoftUefiCertificateAuthority],
        Tpm = CapabilityLevel.Supported,
        SecureBootTemplateLockedAfterTpmInit = true,
        MaxOpticalDrives = 2,
        AuxiliaryMedia = CapabilityLevel.Supported,
        BootOrder = CapabilityLevel.Conditional,
        Console = new(CapabilityLevel.Supported, CapabilityLevel.Supported, CapabilityLevel.Conditional, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, 4 << 20, true),
        Network = new(CapabilityLevel.Supported, CapabilityLevel.Supported, CapabilityLevel.Unsupported, CapabilityLevel.Conditional, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported),
        Suspend = CapabilityLevel.Supported,
        GracefulShutdown = CapabilityLevel.Conditional,
        Notes = ["Simulated backend; no Hyper-V execution."]
    };
    public Task<string?> GetCreationOperationAsync(string name, CancellationToken ct)
    { Check(ct); return Task.FromResult(_creationOperations.GetValueOrDefault(name)); }
    public Task<ChildStoragePlacement> ResolvePrimaryStorageAsync(string name, CancellationToken ct) => ResolveStorageAsync(name, ct);
    public Task<ChildStoragePlacement> ResolveStorageAsync(string name, CancellationToken ct)
    { Check(ct); return Task.FromResult(new ChildStoragePlacement(@"C:\VMs\" + name + ".vhdx", @"C:\", @"C:\")); }
    public Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct) => Task.FromResult(Capabilities);
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure; }
    public Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct) =>
        CreateOwnedAsync(descriptor, Guid.NewGuid().ToString("n"), progress, ct);
    public Task CreateOwnedAsync(ChildVmDescriptor descriptor, string operationId, IProgress<string>? progress, CancellationToken ct)
    {
        Check(ct); HardwarePresets.ValidateCapabilities(descriptor.Hardware, Capabilities, descriptor.AuxiliaryMediaPath is not null);
        if (!_vms.TryAdd(descriptor.Name, (descriptor, Guid.NewGuid().ToString()))) throw new InvalidOperationException("VM already exists.");
        _creationOperations[descriptor.Name] = operationId; _templateLocked[descriptor.Name] = descriptor.Hardware.Tpm; hypervisor.SetState(descriptor.Name, VmState.Off); Calls.Enqueue("create:" + descriptor.Name); if (FailureAfterCreate is not null) throw FailureAfterCreate; return Task.CompletedTask;
    }
    public Task RemoveAsync(string name, IProgress<string>? progress, CancellationToken ct)
    { Check(ct); if (RemoveFailure is not null) throw RemoveFailure; _vms.TryRemove(name, out _); _templateLocked.TryRemove(name, out _); _creationOperations.TryRemove(name, out _); hypervisor.SetState(name, VmState.Absent); Calls.Enqueue("remove:" + name); return Task.CompletedTask; }
    public Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct)
    {
        Check(ct); if (hypervisor.StateOf(name) != VmState.Off) throw new InvalidOperationException("VM must be off.");
        var old = _vms[name]; HardwarePresets.ValidateCapabilities(hardware, Capabilities, old.Descriptor.AuxiliaryMediaPath is not null);
        if (_templateLocked.GetValueOrDefault(name) && resendTemplate) throw new InvalidOperationException("Secure Boot template is locked.");
        if (old.Descriptor.Hardware.Generation != hardware.Generation || old.Descriptor.Hardware.DiskGb != hardware.DiskGb || old.Descriptor.Hardware.NetworkAttached != hardware.NetworkAttached) throw new ChildValidationException("unsupported-capability", "hardware");
        _templateLocked[name] = _templateLocked.GetValueOrDefault(name) || hardware.Tpm;
        _vms[name] = (old.Descriptor with { Hardware = hardware }, old.Id); Calls.Enqueue("hardware:" + name); return Task.CompletedTask;
    }
    public Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct)
    { Check(ct); var old = _vms[name]; _vms[name] = (old.Descriptor with { InstallMediaPath = installMediaPath, AuxiliaryMediaPath = auxiliaryMediaPath, Hardware = old.Descriptor.Hardware with { BootOrder = bootOrder.ToArray() } }, old.Id); Calls.Enqueue("media:" + name); return Task.CompletedTask; }
    public Task<AttachedMedia> GetAttachedMediaAsync(string name, CancellationToken ct)
    { Check(ct); var d = _vms[name].Descriptor; return Task.FromResult(new AttachedMedia(d.InstallMediaPath, d.AuxiliaryMediaPath, true)); }
    public Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    { Check(ct); Calls.Enqueue("shutdown:" + name); if (ShutdownOutcome == GracefulShutdownOutcome.Completed) hypervisor.SetState(name, VmState.Off); return Task.FromResult(ShutdownOutcome); }
    public Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct)
    { Check(ct); var d = _vms.TryGetValue(name, out var found) ? found.Descriptor : new ChildVmDescriptor(name, new(1, 512, 1, 2, false, null, false, [], true), null, null, null, "Default Switch"); return Task.FromResult(new VmCapabilitiesSnapshot(name, hypervisor.StateOf(name), true, true, true, false, 1, 1, _templateLocked.GetValueOrDefault(name), d.Hardware.Generation, Capabilities.GracefulShutdown, new(CapabilityLevel.Supported, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported))); }
    public Task<string?> GetVmIdAsync(string name, CancellationToken ct) => Task.FromResult(_vms.TryGetValue(name, out var vm) ? vm.Id : null);
}
