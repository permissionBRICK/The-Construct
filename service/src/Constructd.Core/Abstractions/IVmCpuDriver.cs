namespace Constructd.Core.Abstractions;

public interface IVmCpuDriver
{
    /// <summary>Changes only the CPU count, requires Off, and verifies the result.</summary>
    Task SetCpuCountAsync(string name, int cpus, CancellationToken ct);
}
