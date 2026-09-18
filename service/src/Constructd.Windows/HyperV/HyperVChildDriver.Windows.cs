using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;
namespace Constructd.Windows.HyperV;
public sealed partial class HyperVChildDriver
{
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
    }
    public async Task EjectWindowsMediaAsync(string name, string incarnation, bool installOnly, CancellationToken ct)
    {
        WindowsIdentity(name, incarnation);
        await RunAsync("windows-eject", "Remove-ConstructWindowsMedia", "-Name $inputData.name -Incarnation $inputData.incarnation -InstallOnly $inputData.installOnly", new { name, incarnation, installOnly }, ct);
    }
}
