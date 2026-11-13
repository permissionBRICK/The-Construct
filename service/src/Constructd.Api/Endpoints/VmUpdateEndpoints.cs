using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Updates;

namespace Constructd.Api.Endpoints;

/// <summary>
/// VMs follow their host's update (12.1, S28): a user reprovisions their own VMs on this host, an admin every VM,
/// a user may let the host do it after it updated itself (<c>updates.autoReprovision</c>), and a VM may ask for
/// its own update with its token (10.1, 11.4). Another user's VM is never touched by these routes.
/// </summary>
public static class VmUpdateEndpoints
{
    public static RouteGroupBuilder MapVmUpdateEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me/updates", GetSettingsAsync).RequireAuthorization(Policies.User)
            .Produces<UserUpdateSettingsBody>().WithName("GetMyUpdateSettings");
        api.MapPut("/me/updates", SetSettingsAsync).RequireAuthorization(Policies.User).Audited("user.updates")
            .Accepts<UserUpdateSettingsBody>("application/json").Produces<UserUpdateSettingsBody>().WithName("SetMyUpdateSettings");
        api.MapPost("/me/vms/reprovision", MineAsync).RequireAuthorization(Policies.User).Audited("vm.reprovision.mine")
            .Produces<VmReprovisionResponse>(StatusCodes.Status202Accepted).WithName("ReprovisionMyVms");
        api.MapPost("/host/vms/reprovision", AllAsync).RequireAuthorization(Policies.Admin).Audited("vm.reprovision.all")
            .Produces<VmReprovisionResponse>(StatusCodes.Status202Accepted).WithName("ReprovisionAllVms");
        api.MapGet("/vms/{name}/update", VmStatusAsync).RequireAuthorization(Policies.VmScoped)
            .Produces<VmUpdateStatusResponse>().WithName("GetVmUpdate");
        api.MapPost("/vms/{name}/update", VmUpdateAsync).RequireAuthorization(Policies.VmScoped).Audited("vm.update")
            .Produces<VmUpdateResponse>(StatusCodes.Status202Accepted).WithName("UpdateVm");
        return api;
    }

    private static async Task<IResult> GetSettingsAsync(HttpContext http, VmUpdates updates, CancellationToken ct) =>
        Results.Ok(new UserUpdateSettingsBody((await updates.SettingsAsync(http.User.NameOrEmpty(), ct)).AutoReprovision));

    private static async Task<IResult> SetSettingsAsync(UserUpdateSettingsBody body, HttpContext http, IHostConfigStore config, CancellationToken ct)
    {
        await config.SetAsync(VmUpdates.SettingsKey(http.User.NameOrEmpty()), new UserUpdateSettings(body.AutoReprovision), http.User.Actor(), ct);
        return Results.Ok(body);
    }

    private static async Task<IResult> MineAsync(HttpContext http, VmUpdates updates, CancellationToken ct) =>
        Results.Accepted(null as string, new VmReprovisionResponse(updates.Start(await updates.PrimariesAsync(http.User.NameOrEmpty(), ct), http.User.Actor())));

    private static async Task<IResult> AllAsync(HttpContext http, VmUpdates updates, CancellationToken ct) =>
        Results.Accepted(null as string, new VmReprovisionResponse(updates.Start(await updates.PrimariesAsync(null, ct), http.User.Actor())));

    /// <summary>That primary's own token, or its owner. Not an admin for someone else's VM: S28.</summary>
    private static async Task<(Vm? Vm, IResult? Denied)> FindAsync(string name, HttpContext http, IVmRepository vms, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return (null, Problems.NotFound("Unknown VM."));
        var user = http.User;
        var allowed = user.IsVmToken()
            ? user.IsPrimaryToken() && Ownership.VmTokenCanAccessVm(user.VmTokenName(), vm.Name)
            : user.IsKnownUser() && Ownership.SameName(vm.Owner, user.NameOrEmpty());
        if (!allowed) return (null, LifecycleEndpoints.Problem("not-owner", 403));
        if (vm.Kind != VmKind.Primary) return (null, LifecycleEndpoints.Problem("not-a-primary"));
        if (vm.Deleting) return (null, LifecycleEndpoints.Problem("vm-deleting"));
        return (vm, null);
    }

    private static async Task<bool> OwnerIsAdminAsync(Vm vm, IUserStore users, CancellationToken ct) =>
        await users.GetAsync(vm.Owner, ct) is { Role: Role.Admin, Enabled: true };

    private static async Task<IResult> VmStatusAsync(string name, HttpContext http, IVmRepository vms, IUserStore users, IVmAppliedStateStore applied,
        IReleaseInfo release, HostUpdateJob work, PackageStager checker, CancellationToken ct)
    {
        var (vm, denied) = await FindAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        var have = await applied.GetAppliedAsync(vm.Name, ct);
        var available = SourceZipRules.ValidCommit(release.Installed.Commit) ? release.Installed.Commit : null;
        HostUpdateOfferResponse? host = null;
        if (await OwnerIsAdminAsync(vm, users, ct) &&
            (await UpdateEndpoints.OfferAsync(work, (await checker.SettingsAsync(ct)).Channel, ct)).Offer is { } offer &&
            offer.Commit != release.Installed.Commit)
            host = new(offer.Version, offer.Commit);
        return Results.Ok(new VmUpdateStatusResponse(
            available is not null && have?.Commit is { } commit && !string.Equals(commit, available, StringComparison.OrdinalIgnoreCase),
            available, have?.Commit, host));
    }

    private static async Task<IResult> VmUpdateAsync(string name, HttpContext http, IVmRepository vms, IUserStore users, IReleaseInfo release,
        HostUpdateJob work, VmUpdates updates, CancellationToken ct)
    {
        var (vm, denied) = await FindAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        try
        {
            // An admin's VM: the host's own update first; once it succeeded the host reprovisions this VM and the
            // owner's other VMs on this host (S28: everything the user owns), never another user's.
            if (await OwnerIsAdminAsync(vm, users, ct))
            {
                var follow = (await updates.PrimariesAsync(vm.Owner, ct)).Select(v => v.Name).Where(n => !Ownership.SameName(n, vm.Name)).Prepend(vm.Name).ToArray();
                await work.Acceptance.WaitAsync(ct);
                try
                {
                    if (await work.RunAsync(http.User.Actor(), ct, follow, vm.Owner) is { } started)
                        return Results.Accepted("/api/v1/jobs/" + started.JobId, new VmUpdateResponse(null, started.UpdateId, started.JobId));
                }
                finally { work.Acceptance.Release(); }
            }
            var job = await updates.SubmitAsync(vm, http.User.Actor(), ct);
            return Results.Accepted("/api/v1/jobs/" + job.Id, new VmUpdateResponse(job.Id, null, null));
        }
        catch (UpdateException ex) { return UpdateEndpoints.RunProblem(http, ex); }
        catch (ProvisionRequestException ex)
        {
            if (ex.Code == "maintenance") return await MaintenanceFilter.RefusedAsync(http);
            return CodedProblems.Create(ex.Status, ex.Code, ex.Message, extra: ex.JobId is null ? null : new() { ["jobId"] = ex.JobId });
        }
    }
}
