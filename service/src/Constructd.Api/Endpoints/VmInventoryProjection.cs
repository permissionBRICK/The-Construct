using System.Security.Claims;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Endpoints;

public sealed class VmInventoryProjection(IVmRepository vms, IVmDelegationRepository delegation, IUserStore users,
    IDelegationPolicy policy, ICapacityLedger capacity, IMediaStore media, IJobStore jobs, IPortForwardManager forwards, ConstructdOptions options, IOperationKeyStore keys, Constructd.Api.Hosting.VmResourceUsageReader usage, PrimaryCpuSettings cpuSettings, PrimaryMemorySettings memorySettings)
{
    public async Task<VmResponse> ProjectAsync(Vm vm, ClaimsPrincipal caller, CancellationToken ct)
    {
        var relationship = await DelegationAuthorization.RelationshipAsync(caller, vm, vms, users, ct);
        var result = await ApiHelpers.ToResponseAsync(vm, forwards, options, ct);
        var owned = relationship is ForwardRelationship.Admin or ForwardRelationship.Owner or ForwardRelationship.Parent or ForwardRelationship.Self;
        var reservation = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).ToArray();
        var mediaProjection = new List<object>();
        foreach (var reference in await media.ListReferencesForVmAsync(vm.Name, ct))
            if (await media.GetAsync(reference.MediaId, ct) is { } item)
                mediaProjection.Add(new { item.Id, item.Role, item.Name, item.SizeBytes, item.State, dedicated = item.DedicatedTo is not null });
        var job = vm.CurrentJobId is null ? null : await jobs.GetAsync(vm.CurrentJobId, ct);
        // The stored pointer survives job completion. Only live jobs hold the VM busy;
        // completed history remains available through the jobs API.
        if (job?.State is not (JobState.Queued or JobState.Running)) job = null;
        var l = vm.Lease;
        var observed = vm.Observed ?? new(null, null, [], null);
        var memorySetting = owned && vm.Kind == VmKind.Primary ? await memorySettings.GetAsync(vm, ct) : null;
        var cpuSetting = owned && vm.Kind == VmKind.Primary ? await cpuSettings.GetAsync(vm, ct) : null;
        // The durable intent is the flag: capacity observation cannot erase an unresolved attachment.
        if (vm.Kind == VmKind.Child && (await keys.ListInFlightAsync(vm.Name, ct)).Any(k => k.Kind == "child-media" && ConfigurationIntent.Applies(k, vm)))
            observed = observed with { StorageProblem = "media-unverified" };
        return result with
        {
            Kind = vm.Kind,
            Parent = vm.Parent,
            Sharing = vm.Sharing,
            Shared = relationship == ForwardRelationship.Shared,
            Incarnation = vm.Incarnation,
            TokenKind = owned && vm.Kind == VmKind.Primary ? vm.TokenKind : null,
            ChildCreationClosed = vm.ChildCreationClosed,
            Lease = l is null ? null : new(l.RequestedText, l.ActivatedAt, l.ExpiresAt, l.State, l.State == LeaseState.Overdue, l.Version, l.LastExpiryAttemptAt, l.LastExpiryOutcome),
            Hardware = vm.Hardware,
            PendingRamGb = memorySetting is not null && memorySetting.RamGb != vm.RamGb ? memorySetting.RamGb : null,
            PendingCpu = cpuSetting is not null && cpuSetting.Cpus != vm.Cpu ? cpuSetting.Cpus : null,
            ResourceUsage = await usage.ReadAsync(vm.Name, ct),
            Media = mediaProjection,
            SourceCommit = vm.SourceCommit,
            Guest = GuestReportRules.ForPresentation(vm.Guest),
            Observed = observed,
            Reservations = new(reservation.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount), checked((int)reservation.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount)), reservation.Where(r => r.Resource == ReservationResource.Storage).Sum(r => r.Amount)),
            CurrentOperation = job is null ? null : new(job.Id, job.Kind, job.Phase, job.Initiator),
            Children = vm.Kind == VmKind.Primary ? (await delegation.ListChildrenAsync(vm.Name, ct)).Select(v => v.Name).ToArray() : null,
            AllowedActions = relationship is null ? [] : await policy.AllowedActionsAsync(vm, caller.NameOrEmpty(), relationship.Value, ct),
            Forwards = owned ? result.Forwards : (await forwards.ListAsync(vm.Name, ct)).Where(f => Ownership.SameName(f.Destination?.RequestedBy, ForwardRequesterHandler.Requester(caller))).Select(f => ForwardResponse.From(f, options.PublicHostFor(vm.Name))).ToArray()
        };
    }
}
