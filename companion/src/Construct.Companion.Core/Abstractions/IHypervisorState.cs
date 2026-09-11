namespace Construct.Companion.Core.Abstractions;

// Read-only local hypervisor state. Absent means "no VM of that name", not "no hypervisor";
// Unknown is what a denied query yields and lets VmPower fall back to Get-VM.
public interface IHypervisorState
{
    Task<HypervisorState> QueryAsync(string vmName, CancellationToken cancellationToken = default);
}
public enum HypervisorState { Running, Off, Saved, Paused, Absent, Unknown }

// Raw CIM Msvm_ComputerSystem EnabledState; HypervisorQuery maps it to HypervisorState.
public sealed record CimVmState(ushort EnabledState);
public interface ICimVmQuery
{
    Task<CimVmState?> QueryAsync(string vmName, CancellationToken cancellationToken = default);
}
