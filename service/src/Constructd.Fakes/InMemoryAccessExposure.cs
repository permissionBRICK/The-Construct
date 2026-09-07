using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed class InMemoryAccessExposure(IVmRepository vms, IPortForwardManager forwards) : IAccessExposure
{
    public async Task<ExposeResult> TryExposeAsync(ForwardRequest request, CancellationToken ct)
    {
        var vm = await vms.GetAsync(request.TargetVm, ct);
        if (vm is null || vm.Deleting) return new(ExposeStatus.VmUnavailable, null, null);
        if (vm.Kind == VmKind.Child) return new(ExposeStatus.AddressUnverifiable, null, "Child address validation is not installed.");
        if (request.Via is not null || request.ConnectPort != request.VmPort) return new(ExposeStatus.PolicyDenied, null, "A primary cannot specify a destination.");
        var result = await forwards.TryAddForwardAsync(vm.Name, request.VmPort, request.Target, request.Label, request.MaxForwards, ct);
        return new(result.Status == AddForwardStatus.Added ? ExposeStatus.Added : ExposeStatus.LimitReached, result.Forward, null);
    }
    public Task<int> RevokeForRequesterAsync(string targetVm, string requesterPrincipal, CancellationToken ct) => RevokeAsync(targetVm, f => f.Destination?.RequestedBy == requesterPrincipal, ct);
    public Task<int> RevokeNonOwnerAsync(string targetVm, CancellationToken ct) => RevokeAsync(targetVm, f => f.Destination?.Relationship == ForwardRelationship.Shared, ct);
    private async Task<int> RevokeAsync(string name, Func<PortForward, bool> match, CancellationToken ct)
    { var count = 0; foreach (var f in (await forwards.ListAsync(name, ct)).Where(match)) if (await forwards.RemoveForwardAsync(name, f.Id, ct)) count++; return count; }
    public async Task<IReadOnlyList<PortForward>> ListViaAsync(string viaVm, CancellationToken ct)
    { var result = new List<PortForward>(); foreach (var vm in await vms.ListAsync(null, ct)) result.AddRange((await forwards.ListAsync(vm.Name, ct)).Where(f => StringComparer.OrdinalIgnoreCase.Equals(f.Destination?.Via, viaVm))); return result; }
}
