using Constructd.Core.Abstractions;
using Constructd.Core.Services;
namespace Constructd.Fakes;

/// <summary>The same policy/address logic as production, backed by the fake manager and address provider.</summary>
public sealed class InMemoryAccessExposure(IVmRepository vms, IUserStore users, IPortForwardManager forwards,
    IForwardStore store, GuestAddressResolver addresses, IHostNetworkPolicy policy, INetworkRuleStore history,
    IClock clock, IAuditLog audit) : AccessExposure(vms, users, forwards, store, addresses, policy, history, clock, audit);
