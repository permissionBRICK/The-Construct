using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// Ownership rules: a user sees and manages only their own VMs, an admin sees all (plan §4.4).
/// Names are compared case-insensitively because Windows identities (<c>DOMAIN\user</c>) are.
/// </summary>
public static class Ownership
{
    public static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    public static bool SameName(string? left, string? right) =>
        left is not null && right is not null && NameComparer.Equals(left, right);

    /// <summary>May this principal read/manage the VM?</summary>
    public static bool CanAccessVm(string? principalName, Role role, Vm vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return CanAccessVm(principalName, role, vm.Owner);
    }

    public static bool CanAccessVm(string? principalName, Role role, string vmOwner) =>
        role == Role.Admin || SameName(principalName, vmOwner);

    /// <summary>May a VM-scoped token act on this VM? Only on its own, never on another.</summary>
    public static bool VmTokenCanAccessVm(string? tokenVmName, string vmName) =>
        tokenVmName is not null && string.Equals(tokenVmName, vmName, StringComparison.Ordinal);

    /// <summary>Quota check for <c>POST /vms</c>. <paramref name="ownedVms"/> is the current count.</summary>
    public static bool CanCreateVm(User user, int ownedVms)
    {
        ArgumentNullException.ThrowIfNull(user);
        return ownedVms < user.MaxVms;
    }
}
