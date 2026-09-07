using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class BootOrderRules
{
    public static IReadOnlyList<BootDevice> Resolve(IReadOnlyList<BootDevice>? requested, bool auxiliary, bool network)
    {
        BootDevice[] defaults = [BootDevice.InstallMedia, BootDevice.AuxiliaryMedia, BootDevice.Disk, BootDevice.Network];
        var order = (requested ?? defaults.Where(x => (x != BootDevice.AuxiliaryMedia || auxiliary) && (x != BootDevice.Network || network)).ToArray()).ToArray();
        if (order.Distinct().Count() != order.Length || order.Any(x => !Enum.IsDefined(x)) ||
            !order.Contains(BootDevice.InstallMedia) || !order.Contains(BootDevice.Disk) ||
            (!auxiliary && order.Contains(BootDevice.AuxiliaryMedia)) || (!network && order.Contains(BootDevice.Network)))
            throw new ChildValidationException("validation", "bootOrder");
        return order;
    }
}
