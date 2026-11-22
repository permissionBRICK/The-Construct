using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// Which VMs a host brings back when it boots. A child is disposable and never comes back on its own. A
/// primary comes back only while its effective idle policy (the host's cap and forced idling included) is
/// off: a VM that idles out is meant to stay down until someone uses it again, so a host boot must not wake
/// it, also not from the saved state the idle action left it in.
/// </summary>
public static class HostStartRules
{
    public static bool StartsWithHost(Vm vm, IdleOptions idle)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return vm.Kind == VmKind.Primary && IdlePolicyRules.Clamp(vm.IdlePolicy, idle).IsDisabled;
    }
}
