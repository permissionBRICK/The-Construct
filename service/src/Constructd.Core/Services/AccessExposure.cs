using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Core.Services;

/// <summary>Common exposure path: platform managers allocate; the Hyper-V address seam never verifies ownership.</summary>
public class AccessExposure(IVmRepository vms, IUserStore users, IPortForwardManager forwards, IForwardStore store,
    GuestAddressResolver addresses, IHostNetworkPolicy policy, INetworkRuleStore history, IClock clock, IAuditLog audit) : IAccessExposure
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<ExposeResult> TryExposeAsync(ForwardRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var vm = await vms.GetAsync(request.TargetVm, ct);
            if (vm is null || vm.Deleting) return new(ExposeStatus.VmUnavailable, null, null);
            if (vm.Kind != VmKind.Child) return new(ExposeStatus.PolicyDenied, null, "Primary forwards use the legacy path.");
            if (request.Target == ForwardTarget.Host)
            {
                if (!(await policy.GetAsync(ct)).HostForwardsEnabled || await users.GetAsync(vm.Owner, ct) is not { AllowHostForwards: true })
                    return new(ExposeStatus.PolicyDenied, null, "host-forwards-disabled");
                return new(ExposeStatus.AddressUnverifiable, null, "no-address-authority");
            }
            if (request.Via is null) return new(ExposeStatus.PolicyDenied, null, "via-required");
            var selected = await addresses.SelectAsync(vm, request.Via, ct);
            if (selected.Conflict) return new(ExposeStatus.AddressUnverifiable, null, "address-conflict");
            var destination = new ForwardDestination(vm.Name, request.Via, selected.Address, request.ConnectPort,
                request.RequesterPrincipal, request.Relationship, false);
            if (selected.Address is not null) await history.RememberAddressAsync(vm.Name, selected.Address, ct);
            var result = await forwards.TryAddDestinationForwardAsync(request, destination, ct);
            if (result.Forward is { } forward && selected.Address is null)
            {
                var ack = new ForwardAck(AckStatus.Error, null, null, "guest address unknown yet", clock.UtcNow);
                await store.SetAckAsync(forward.Id, ack, ct);
                result = result with { Forward = forward with { Ack = ack } };
            }
            return new(result.Status switch { AddForwardStatus.Added => ExposeStatus.Added, AddForwardStatus.VmUnavailable => ExposeStatus.VmUnavailable, _ => ExposeStatus.LimitReached }, result.Forward, null);
        }
        finally { _gate.Release(); }
    }
    public Task<int> RevokeForRequesterAsync(string targetVm, string requesterPrincipal, CancellationToken ct) =>
        RevokeAsync(targetVm, f => Ownership.SameName(f.Destination?.RequestedBy, requesterPrincipal), ct);
    public Task<int> RevokeNonOwnerAsync(string targetVm, CancellationToken ct) =>
        RevokeAsync(targetVm, f => f.Destination?.Relationship == ForwardRelationship.Shared, ct);
    private async Task<int> RevokeAsync(string target, Func<PortForward, bool> predicate, CancellationToken ct)
    {
        var count = 0;
        foreach (var forward in (await forwards.ListAsync(target, ct)).Where(predicate))
            if (await forwards.RemoveForwardAsync(target, forward.Id, ct))
            { count++; await AuditAsync(forward, "requester revoked", ct); }
        return count;
    }
    public async Task<IReadOnlyList<PortForward>> ListViaAsync(string viaVm, CancellationToken ct) =>
        (await forwards.ListAsync(null, ct)).Where(f => Ownership.SameName(f.Destination?.Via, viaVm)).ToArray();

    /// <summary>Revocation and address changes affect only child rows; legacy primary rules remain unchanged.</summary>
    public async Task<int> ReconcileAsync(CancellationToken ct, IGuestAddressProvider? snapshot = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var count = 0;
            var children = (await store.ListAsync(null, ct)).Where(f => f.Destination is not null).ToArray();
            if (children.Length == 0) return 0;
            snapshot ??= await addresses.CaptureAsync(ct);
            foreach (var forward in children)
            {
                var d = forward.Destination!;
                var target = await vms.GetAsync(d.VmName, ct);
                var via = d.Via is null ? null : await vms.GetAsync(d.Via, ct);
                if (target is not { Kind: VmKind.Child, Deleting: false } ||
                    await users.GetAsync(target.Owner, ct) is not { Enabled: true } ||
                    via is not { Kind: VmKind.Primary, Deleting: false } || await users.GetAsync(via.Owner, ct) is not { Enabled: true } ||
                    d.Relationship == ForwardRelationship.Shared && target.Sharing != SharingScope.Host ||
                    !await RequesterStillAllowedAsync(d, target, via, ct))
                {
                    if (await forwards.RemoveForwardAsync(forward.VmName, forward.Id, ct))
                    { count++; await AuditAsync(forward, "revoked", ct); }
                    continue;
                }
                var selected = await addresses.SelectAsync(target, via.Name, ct, snapshot);
                if (!StringComparer.Ordinal.Equals(d.ConnectAddress, selected.Address))
                {
                    if (selected.Address is not null) await history.RememberAddressAsync(target.Name, selected.Address, ct);
                    await store.SetDestinationAsync(forward.Id, d with { ConnectAddress = selected.Address, Verified = false },
                        selected.Address is null ? new(AckStatus.Error, null, null, "guest address changed", clock.UtcNow) : null, ct);
                    count++;
                    await AuditAsync(forward, "guest address changed", ct);
                }
            }
            return count;
        }
        finally { _gate.Release(); }
    }
    private Task AuditAsync(PortForward forward, string reason, CancellationToken ct) => audit.AppendAsync(
        new(clock.UtcNow, "system", "forward.reconcile", forward.VmName, AuditOutcome.Success,
            "id=" + forward.Id + ", reason=" + reason), ct);

    /// <summary>Compare destination and validate under the same gate as address reconciliation.</summary>
    public async Task<bool> TryAckAsync(PortForward expected, ForwardAck ack, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await store.GetAsync(expected.Id, ct);
            if (current?.Destination is not { } d || current.Destination != expected.Destination ||
                await vms.GetAsync(d.VmName, ct) is not { Kind: VmKind.Child, Deleting: false } target ||
                d.Via is null || await vms.GetAsync(d.Via, ct) is not { Kind: VmKind.Primary, Deleting: false } via ||
                !await RequesterStillAllowedAsync(d, target, via, ct)) return false;
            var selected = await addresses.SelectAsync(target, via.Name, ct);
            if (selected.Address != d.ConnectAddress)
            {
                if (selected.Address is not null) await history.RememberAddressAsync(target.Name, selected.Address, ct);
                await store.SetDestinationAsync(current.Id, d with { ConnectAddress = selected.Address, Verified = false },
                    selected.Address is null ? new(AckStatus.Error, null, null, "guest address changed", clock.UtcNow) : null, ct);
                await AuditAsync(current, "guest address changed", ct);
                return false;
            }
            if (ack.Status == AckStatus.Open && selected.Address is null) return false;
            return await store.SetAckAsync(current.Id, ack, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> RequesterStillAllowedAsync(ForwardDestination d, Vm target, Vm via, CancellationToken ct)
    {
        if (d.RequestedBy.StartsWith("vm:", StringComparison.OrdinalIgnoreCase))
        {
            var parent = await vms.GetAsync(d.RequestedBy[3..], ct);
            return parent is { Kind: VmKind.Primary, TokenKind: VmTokenKind.Primary, VmTokenHash: not null, Deleting: false } &&
                Ownership.SameName(parent.Name, via.Name) &&
                (Ownership.SameName(target.Parent, parent.Name) || target.Sharing == SharingScope.Host);
        }
        var requester = await users.GetAsync(d.RequestedBy, ct);
        return requester is { Enabled: true } && Ownership.SameName(requester.Name, via.Owner) &&
            (requester.Role == Role.Admin || Ownership.SameName(requester.Name, target.Owner) || target.Sharing == SharingScope.Host);
    }
}
