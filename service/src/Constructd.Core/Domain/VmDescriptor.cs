namespace Constructd.Core.Domain;

/// <summary>
/// What the hypervisor driver needs to create a VM (plan §4.2 <c>New-Vm(descriptor)</c>).
/// </summary>
/// <param name="StartWithHost">Whether a host boot brings the VM back (<see cref="Logic.HostStartRules"/>).</param>
public sealed record VmDescriptor(
    string Name,
    int Cpu,
    int RamGb,
    int DiskGb,
    string? IsoPath,
    bool Nested,
    bool AutomaticCheckpoints,
    bool StartWithHost = false);
