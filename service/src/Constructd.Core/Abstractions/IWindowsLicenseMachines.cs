using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

public interface IWindowsLicenseMachines
{
    Task ParkWindowsAsync(string name, string incarnation, CancellationToken ct);
    Task ReuseWindowsAsync(WindowsLicenseMachine machine, ChildVmDescriptor descriptor, string operationId, CancellationToken ct);
    Task RetireWindowsAsync(string incarnation, CancellationToken ct);
    Task DeliverActivationAsync(string name, string incarnation, WindowsActivationCommand command, CancellationToken ct);
    Task<bool> ActivationProviderReadyAsync(CancellationToken ct);
    Task<string> AcquireConfirmationIdAsync(string kind, WindowsActivationReport report, CancellationToken ct);
}
