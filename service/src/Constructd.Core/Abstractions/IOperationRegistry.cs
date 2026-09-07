using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IOperationRegistry
{
    IDisposable Register(string operationId, string kind, string? vmName);
    bool IsAlive(string operationId);
    IReadOnlyList<(string OperationId, string Kind, string? VmName)> Alive();
}
