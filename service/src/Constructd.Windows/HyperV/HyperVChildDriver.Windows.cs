using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Core.Domain;
using Constructd.Windows.Internal;
namespace Constructd.Windows.HyperV;
public sealed partial class HyperVChildDriver
{
    public async Task ParkWindowsAsync(string name, string incarnation, CancellationToken ct)
    {
        WindowsIdentity(name, incarnation);
        await RunAsync("windows-allocation-clear", "Clear-ConstructLicenseAllocation", "-Name $inputData.name -Incarnation $inputData.incarnation -VhdPath $inputData.vhdPath",
            new { name, incarnation, vhdPath = DiskPath(name) }, ct, TimeSpan.FromMinutes(30));
        await RunAsync("windows-park", "Save-ConstructLicenseMachine", "-Name $inputData.name -Incarnation $inputData.incarnation -VhdPath $inputData.vhdPath",
            new { name, incarnation, vhdPath = DiskPath(name) }, ct, TimeSpan.FromMinutes(30));
    }
    public async Task ReuseWindowsAsync(WindowsLicenseMachine machine, ChildVmDescriptor descriptor, string operationId, CancellationToken ct)
    {
        ValidateDescriptor(descriptor);
        await RunAsync("windows-reuse", "Restore-ConstructLicenseMachine", "-Incarnation $inputData.incarnation -Descriptor $inputData.descriptor -OperationId $inputData.operationId -PoolRoot $inputData.poolRoot",
            new { incarnation = machine.Incarnation, descriptor, operationId, poolRoot = options.VmStorageRoot }, ct, TimeSpan.FromMinutes(30));
    }
    public async Task RetireWindowsAsync(string incarnation, CancellationToken ct) =>
        await RunAsync("windows-retire", "Remove-ConstructLicenseMachine", "-Incarnation $inputData.incarnation -PoolRoot $inputData.poolRoot", new { incarnation, poolRoot = options.VmStorageRoot }, ct);
    public async Task DeliverActivationAsync(string name, string incarnation, WindowsActivationCommand command, CancellationToken ct)
    {
        WindowsIdentity(name, incarnation);
        await RunAsync("windows-activation", "Set-ConstructWindowsActivation", "-Name $inputData.name -Incarnation $inputData.incarnation -Command $inputData.command", new { name, incarnation, command }, ct);
    }
    public async Task<bool> ActivationProviderReadyAsync(CancellationToken ct) =>
        Read<bool>(await RunAsync("windows-provider", "Test-ConstructVamt", "-ModulePath $inputData.modulePath", new { modulePath = options.VamtModulePath }, ct));
    public async Task<string> AcquireConfirmationIdAsync(string kind, WindowsActivationReport report, CancellationToken ct) =>
        Read<string>(await RunAsync("windows-confirmation", "Get-ConstructConfirmationId", "-ModulePath $inputData.modulePath -Kind $inputData.kind -Report $inputData.report", new { modulePath = options.VamtModulePath, kind, report }, ct, TimeSpan.FromMinutes(5)));
    private static void WindowsIdentity(string name, string incarnation)
    { ArgumentGuard.VmName(name); if (!Guid.TryParse(incarnation, out _)) throw new ChildValidationException("validation", "incarnation"); }
    public async Task<WindowsGuestObservation> ObserveWindowsAsync(string name, string incarnation, CancellationToken ct)
    {
        WindowsIdentity(name, incarnation);
        return Read<WindowsGuestObservation>(await RunAsync("windows-observe", "Get-ConstructWindowsGuest", "-Name $inputData.name -Incarnation $inputData.incarnation", new { name, incarnation }, ct));
    }
    public async Task DeliverWindowsKeyAsync(string name, string incarnation, string key, CancellationToken ct)
    {
        WindowsIdentity(name, incarnation);
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"\A[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}\z")) throw new ChildValidationException("validation", "key");
        await RunAsync("windows-key", "Set-ConstructWindowsKey", "-Name $inputData.name -Incarnation $inputData.incarnation -Key $inputData.key", new { name, incarnation, key }, ct);
    }
    public async Task ClearWindowsKeyAsync(string name, string incarnation, CancellationToken ct)
    {
        WindowsIdentity(name, incarnation);
        await RunAsync("windows-key-clear", "Set-ConstructWindowsKey", "-Name $inputData.name -Incarnation $inputData.incarnation -Key ''", new { name, incarnation }, ct);
        await RunAsync("windows-activation-clear", "Set-ConstructWindowsActivation", "-Name $inputData.name -Incarnation $inputData.incarnation -Command $null", new { name, incarnation }, ct);
    }
    public async Task EjectWindowsMediaAsync(string name, string incarnation, bool installOnly, CancellationToken ct)
    {
        WindowsIdentity(name, incarnation);
        await RunAsync("windows-eject", "Remove-ConstructWindowsMedia", "-Name $inputData.name -Incarnation $inputData.incarnation -InstallOnly $inputData.installOnly", new { name, incarnation, installOnly }, ct);
    }
}
