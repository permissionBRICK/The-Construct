using System.Management;
using System.Runtime.Versioning;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class CimVmQuery : ICimVmQuery
{
    public Task<CimVmState?> QueryAsync(string vmName, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Enumerate VM rows and compare the name as data, avoiding WQL interpolation.
        using var query = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\virtualization\v2"),
            new ObjectQuery("SELECT ElementName, EnabledState FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'"),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = false });
        using var rows = query.Get();
        CimVmState? result = null;
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(row["ElementName"] as string, vmName, StringComparison.OrdinalIgnoreCase))
                    result = new(Convert.ToUInt16(row["EnabledState"]));
            }
        }
        return result;
    }, cancellationToken);
}
