namespace Constructd.Core.Abstractions;

public interface IVmMemoryDriver
{
    /// <summary>Changes fixed startup RAM, requires Off, and verifies the result.</summary>
    Task SetMemoryAsync(string name, int ramGb, CancellationToken ct);
}
