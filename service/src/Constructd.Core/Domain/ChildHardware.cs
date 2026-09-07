namespace Constructd.Core.Domain;

/// <param name="DynamicMemory">Reserved seam for a future backend; every current backend refuses a non-null value.</param>
public sealed record ChildHardware(
    int Cpus,
    int RamMb,
    int DiskGb,
    int Generation,
    bool SecureBoot,
    SecureBootTemplate? SecureBootTemplate,
    bool Tpm,
    IReadOnlyList<BootDevice> BootOrder,
    bool NetworkAttached,
    DynamicMemoryPolicy? DynamicMemory = null);

public sealed record DynamicMemoryPolicy(long MinimumBytes, long StartupBytes, long MaximumBytes);
