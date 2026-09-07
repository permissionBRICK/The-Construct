using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

/// <summary>Lease half of per-VM reconciliation. Caller holds the VM gate and supplies fresh driver state.</summary>
public interface IChildLeaseReconciler
{
    Task ReconcileAsync(Vm vm, VmState state, CancellationToken ct);
}
