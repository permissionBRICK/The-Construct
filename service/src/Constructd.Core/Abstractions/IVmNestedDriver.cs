namespace Constructd.Core.Abstractions;

public interface IVmNestedDriver
{
    Task<bool> GetNestedAsync(string name, CancellationToken ct);
    Task SetNestedAsync(string name, bool enabled, CancellationToken ct);
}
