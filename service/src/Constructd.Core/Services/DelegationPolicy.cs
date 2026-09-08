using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Core.Services;

public sealed class DelegationPolicy(IUserStore users, IVmRepository vms, IVmDelegationRepository delegation,
    IHostConfigStore config, ICapabilityAggregator capabilities) : IDelegationPolicy
{
    public async Task<EffectiveAllowance> ResolveAsync(string owner, string? parentVm, CancellationToken ct)
    {
        var user = await users.GetAsync(owner, ct) ?? throw new InvalidOperationException("Owner is not enrolled.");
        var d = await config.GetAsync<UserDefaultsConfig>("userDefaults", ct) ?? HostAdminDefaults.UserDefaults;
        var c = await config.GetAsync<UserCapsConfig>("userCaps", ct) ?? HostAdminDefaults.UserCaps;
        var network = await config.GetAsync<NetworkConfig>("network", ct) ?? HostAdminDefaults.Network;
        var u = user.Allowance ?? UserAllowance.Unset;
        VmOverride? o = null;
        var parent = parentVm is null ? null : await vms.GetAsync(parentVm, ct);
        if (parent is not null && Ownership.SameName(parent.Owner, owner)) o = await delegation.GetOverrideAsync(parent.Name, ct);
        var enabled = user.Enabled; // Resource fences are checked separately from policy allowances.
        return new(user.MaxVms, enabled && (u.AllowChildCreation ?? d.AllowChildCreation) && o?.AllowChildCreation != false,
            (int)Min(Min(u.MaxRetainedChildren ?? d.MaxRetainedChildren, c.MaxRetainedChildren), o?.MaxRetainedChildren)!,
            (int?)Min(u.CpuBudget ?? d.CpuBudget, c.CpuBudget), Min(u.RamBudgetBytes ?? d.RamBudgetBytes, c.RamBudgetBytes),
            Min(u.StorageBudgetBytes ?? d.StorageBudgetBytes, c.StorageBudgetBytes),
            Min(Min(u.MaxChildLifetimeSeconds ?? d.MaxChildLifetimeSeconds, c.MaxChildLifetimeSeconds), o?.MaxChildLifetimeSeconds),
            enabled && (u.AllowNeverLifetime ?? d.AllowNeverLifetime) && c.AllowNeverLifetime != false && o?.AllowNeverLifetime != false,
            enabled && (u.AllowSharing ?? d.AllowSharing) && c.AllowSharing != false && o?.AllowSharing != false,
            user.Enabled && user.AllowHostForwards && network.HostForwardsEnabled);
    }
    private static long? Min(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    public async Task<AllowanceUsage> UsageAsync(string owner, CancellationToken ct)
    {
        var owned = await vms.ListAsync(owner, ct);
        var active = owned.Where(v => v.State is not (VmState.Off or VmState.Saved)).ToArray();
        return new(owned.Count(v => v.Kind == VmKind.Primary), owned.Count(v => v.Kind == VmKind.Child),
            active.Sum(v => v.Cpu), active.Sum(v => v.RamBytes), owned.Sum(v => v.DiskGb * 1073741824L));
    }
    public async Task<IReadOnlyList<ChildAction>> AllowedActionsAsync(Vm vm, string principal, ForwardRelationship relationship, CancellationToken ct)
    {
        var owner = await users.GetAsync(vm.Owner, ct);
        var token = principal.StartsWith("vm:", StringComparison.Ordinal);
        if (token)
        {
            var parent = await vms.GetAsync(principal[3..], ct);
            if (parent is not { Kind: VmKind.Primary, TokenKind: VmTokenKind.Primary, Deleting: false } ||
                await users.GetAsync(parent.Owner, ct) is not { Enabled: true }) return [];
        }
        else if (await users.GetAsync(principal, ct) is not { Enabled: true }) return [];
        if (relationship == ForwardRelationship.Shared && (vm.Sharing != SharingScope.Host || owner is not { Enabled: true })) return [];
        var actions = new List<ChildAction> { ChildAction.Inspect };
        if (vm.Deleting) return actions;
        var own = relationship != ForwardRelationship.Shared;
        var admin = relationship == ForwardRelationship.Admin;
        var effective = await ResolveAsync(vm.Owner, vm.Kind == VmKind.Primary ? vm.Name : vm.Parent, ct);
        if (vm.Kind == VmKind.Primary)
        {
            actions.Add(ChildAction.ForwardClient);
            if (effective.AllowHostForwards) actions.Add(ChildAction.ForwardHost);
            if (!token) actions.AddRange([ChildAction.Start, ChildAction.Delete, ChildAction.RotateToken]);
            if (!token)
            {
                var primaryCaps = await capabilities.GetAsync(ct);
                if (primaryCaps.Legacy.Suspend) actions.Add(ChildAction.Save);
                if (primaryCaps.GracefulShutdown != CapabilityLevel.Unsupported) actions.Add(ChildAction.Shutdown);
            }
            if (admin) actions.Add(ChildAction.Overrides);
            return actions;
        }
        var caps = await capabilities.GetAsync(ct);
        if (caps.Generations.Count > 0)
        {
            actions.AddRange([ChildAction.Start, ChildAction.Restart]);
            if (own) actions.AddRange([ChildAction.Delete, ChildAction.Renew, ChildAction.Hardware, ChildAction.Media]);
            if (own && effective.AllowSharing) actions.Add(ChildAction.Share);
        }
        if (caps.GracefulShutdown != CapabilityLevel.Unsupported) actions.Add(ChildAction.Shutdown);
        if (caps.Suspend != CapabilityLevel.Unsupported) actions.Add(ChildAction.Save);
        if (caps.Console.Screenshot != CapabilityLevel.Unsupported) actions.Add(ChildAction.Console);
        if (caps.Network.ClientForward != CapabilityLevel.Unsupported) actions.Add(ChildAction.ForwardClient);
        if (caps.Network.HostForwardChild != CapabilityLevel.Unsupported && caps.Network.AddressVerification != CapabilityLevel.Unsupported && effective.AllowHostForwards) actions.Add(ChildAction.ForwardHost);
        if (caps.Network.DirectAddressReporting != CapabilityLevel.Unsupported) actions.Add(ChildAction.Addresses);
        return actions;
    }
}
