using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

public static class NestedPolicy
{
    public static bool MaySelect(User user, VirtualizationConfig config) =>
        user.Role == Role.Admin || (user.AllowNested ?? config.NestedSelectable);
}
