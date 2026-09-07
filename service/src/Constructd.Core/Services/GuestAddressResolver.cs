using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Core.Services;

public sealed record GuestAddressSelection(string? Address, bool Conflict);
public sealed class GuestAddressResolver(IGuestAddressProvider addresses, IVmRepository vms, INetworkRuleStore history)
{
    public Task<IGuestAddressProvider> CaptureAsync(CancellationToken ct) => addresses is IGuestAddressSnapshotProvider snapshots
        ? snapshots.CaptureAsync(ct) : Task.FromResult(addresses);
    public async Task<GuestAddressSelection> SelectAsync(Vm target, string via, CancellationToken ct, IGuestAddressProvider? snapshot = null)
    {
        var facts = snapshot ?? await CaptureAsync(ct);
        var reported = await facts.GetReportedAddressesAsync(target.Name, ct);
        var targetAdapters = await facts.GetAdaptersAsync(target.Name, ct);
        // An incarnation is mandatory: a VM replaced outside the service must not inherit connectivity.
        if (target.Incarnation is null || targetAdapters.Any(a => !StringComparer.OrdinalIgnoreCase.Equals(a.VmId, target.Incarnation))) return new(null, false);
        var viaVm = await vms.GetAsync(via, ct);
        if (viaVm is not { Kind: VmKind.Primary, Deleting: false }) return new(null, false);
        var viaAdapters = await facts.GetAdaptersAsync(via, ct);
        if (viaVm.Incarnation is not null && viaAdapters.Any(a => !StringComparer.OrdinalIgnoreCase.Equals(a.VmId, viaVm.Incarnation))) return new(null, false);
        var subnets = await facts.GetGuestSubnetsAsync(ct);
        var host = await facts.GetHostAddressesAsync(ct);
        if (host.Count == 0) return new(null, false); // Missing host evidence must fail closed.
        var forbidden = new HashSet<System.Net.IPAddress>();
        foreach (var other in await vms.ListAsync(null, ct))
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(other.Name, target.Name)) continue;
            foreach (var text in (await facts.GetReportedAddressesAsync(other.Name, ct)).Select(a => a.Address)
                .Concat(await history.PreviousAddressesAsync(other.Name, ct)))
                if (GuestAddressRules.Parse(text) is { } ip) forbidden.Add(ip);
        }
        var conflict = false;
        foreach (var candidate in reported)
        {
            if (GuestAddressRules.Parse(candidate.Address) is not { } ip) continue;
            if (forbidden.Contains(ip)) { conflict = true; continue; }
            var reportingAdapters = candidate.AdapterId is null
                ? (targetAdapters.Count == 1 ? targetAdapters : [])
                : targetAdapters.Where(a => StringComparer.OrdinalIgnoreCase.Equals(a.AdapterId, candidate.AdapterId)).ToArray();
            if (GuestAddressRules.Usable(ip, host, reportingAdapters, viaAdapters, subnets)) return new(ip.ToString(), false);
        }
        return new(null, conflict);
    }
}
