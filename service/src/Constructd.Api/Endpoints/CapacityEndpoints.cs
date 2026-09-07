using Constructd.Api.Auth;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Endpoints;

public static class CapacityEndpoints
{
    public static IResult Refusal(CapacityDecision decision, IReadOnlyList<string>? problems = null)
    {
        if (decision.Allowed) throw new ArgumentException("An allowed decision is not a refusal.");
        var code = decision.Reason == "inventory-incomplete" ? "capacity-unavailable" : decision.Reason == "operation-in-progress" ? "operation-in-progress" : "capacity-exhausted";
        return Results.Problem(statusCode: 409, title: code, type: "urn:construct:problem:" + code,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["resource"] = decision.Resource, ["scope"] = decision.Scope,
                ["requested"] = decision.Requested, ["allowed"] = decision.AllowedAmount, ["available"] = decision.Available,
                ["reason"] = decision.Reason, ["epoch"] = decision.Epoch, ["problems"] = problems ?? [] });
    }
    public static RouteGroupBuilder MapCapacityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/host/capacity", async (bool? refresh, ICapacityLedger ledger, IVmRepository vms, IUserStore users, CancellationToken ct) =>
        {
            var snapshot = await ledger.SnapshotAsync(refresh == true, ct);
            var managed = await vms.ListAsync(null, ct);
            var owners = (await users.ListAsync(ct)).Select(u => u.Name).Concat(snapshot.Reservations.Where(r => r.ScopeOwner is not null).Select(r => r.ScopeOwner!))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                summary = HostAdminEndpoints.CapacitySummary(snapshot),
                problems = snapshot.Problems ?? (snapshot.Complete ? [] : new[] { "inventory-incomplete" }),
                reservations = snapshot.Reservations.Select(r => new { r.Id, r.Resource, r.ScopeOwner, r.VmName, r.Artifact, r.Volume, r.Amount, r.Phase, r.Origin, r.OperationId, r.PendingUntil, r.Created }),
                unmanaged = snapshot.Unmanaged.Select(v => new { v.Name, v.Id, v.State, v.Cpus, v.MemoryStartupBytes, v.MemoryAssignedBytes, v.DynamicMemory, v.MemoryMaximumBytes,
                    disks = v.Disks.Select(d => new { d.Path, d.MaxBytes, d.FileBytes, d.Readable }) }),
                perUser = owners.Select(owner =>
                {
                    var rows = snapshot.Reservations.Where(r => Ownership.SameName(r.ScopeOwner, owner)).ToArray();
                    return new { user = owner, primaries = managed.Count(v => Ownership.SameName(v.Owner, owner) && v.Kind == VmKind.Primary),
                        children = managed.Count(v => Ownership.SameName(v.Owner, owner) && v.Kind == VmKind.Child),
                        cpus = rows.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount),
                        ramBytes = rows.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount),
                        storageBytes = rows.Where(r => r.Resource == ReservationResource.Storage).Sum(r => r.Amount) };
                })
            });
        }).RequireAuthorization(Policies.Admin).WithName("HostCapacity");
        return api;
    }
}
