using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Desktop;

public sealed class HypervisorQuery(ICimVmQuery cim) : IHypervisorState
{
    public static HypervisorState Map(CimVmState? state) => state?.EnabledState switch
    {
        null => HypervisorState.Absent, 2 => HypervisorState.Running, 3 => HypervisorState.Off,
        32768 => HypervisorState.Paused, 32769 => HypervisorState.Saved, _ => HypervisorState.Unknown
    };
    public async Task<HypervisorState> QueryAsync(string vmName, CancellationToken cancellationToken = default)
        => Map(await cim.QueryAsync(vmName, cancellationToken));
}
