using System.Text;
using System.Text.Json.Nodes;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Profiles;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Store.Records;
using Constructd.Sync.Replica;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Project profiles through the engine (docs/plans/construct-platform-design.md 9.3, 9.6, 10.1). A
/// VM writes with its own token into its owner's profiles; a device submits its edit to an engine
/// (devices never merge, S9) and reads profiles and open conflicts here.
/// </summary>
public static class ProfileEndpoints
{
    public static RouteGroupBuilder MapProfileEndpoints(this RouteGroupBuilder api)
    {
        // ---- a VM, with its own token ----
        api.MapPut("/vms/{name}/profiles/{profile}", PutFromVmAsync)
            .RequireAuthorization(Policies.VmToken).Audited("vm.profile.write").WithName("PutVmProfile").Produces<ProfileWriteResponse>();
        api.MapDelete("/vms/{name}/profiles/{profile}", DeleteFromVmAsync)
            .RequireAuthorization(Policies.VmToken).Audited("vm.profile.delete").WithName("DeleteVmProfile").Produces<ProfileWriteResponse>();
        api.MapGet("/vms/{name}/profile-conflicts", VmConflictsAsync)
            .RequireAuthorization(Policies.VmToken).WithName("ListVmProfileConflicts").Produces<List<ProfileConflictResponse>>();
        api.MapPost("/vms/{name}/profile-conflicts/{id}/resolve", ResolveFromVmAsync)
            .RequireAuthorization(Policies.VmToken).Audited("vm.profile.resolve").WithName("ResolveVmProfileConflict").Produces<ProfileWriteResponse>();

        // ---- a device ----
        api.MapGet("/profiles", ListAsync)
            .RequireAuthorization(Policies.User).WithName("ListProfiles").Produces<List<ProfileSummary>>();
        api.MapGet("/profiles/{profile}", GetAsync)
            .RequireAuthorization(Policies.User).WithName("GetProfile").Produces<ProfileResponse>();
        api.MapPut("/profiles/{profile}", PutFromDeviceAsync)
            .RequireAuthorization(Policies.User).Audited("profile.write", "profile").WithName("PutProfile").Produces<ProfileWriteResponse>();
        api.MapDelete("/profiles/{profile}", DeleteFromDeviceAsync)
            .RequireAuthorization(Policies.User).Audited("profile.delete", "profile").WithName("DeleteProfile").Produces<ProfileWriteResponse>();
        api.MapGet("/profile-conflicts", DeviceConflictsAsync)
            .RequireAuthorization(Policies.User).WithName("ListProfileConflicts").Produces<List<ProfileConflictResponse>>();
        api.MapPost("/profile-conflicts/{id}/resolve", ResolveFromDeviceAsync)
            .RequireAuthorization(Policies.User).Audited("profile.resolve", "id").WithName("ResolveProfileConflict").Produces<ProfileWriteResponse>();
        return api;
    }

    // ---- VM routes ------------------------------------------------------------------------------

    /// <summary>The VM of the route, when the caller is that VM's own token.</summary>
    private static async Task<(Vm? Vm, IResult? Failure)> OwnVmAsync(HttpContext http, string name, IVmRepository vms, IAuthorizationService authorization, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.VmSelfOrOwnerOrAdmin, ct);
        if (!lookup.Ok) return (null, lookup.Failure);
        if (!string.Equals(http.User.VmTokenName(), lookup.Vm!.Name, StringComparison.Ordinal))
            return (null, Problems.Forbidden($"Only VM '{name}' writes its profiles here."));
        return ApiHelpers.FenceDeleting(lookup.Vm) is { } fenced ? (null, fenced) : (lookup.Vm, null);
    }

    private static async Task<IResult> PutFromVmAsync(string name, string profile, [FromBody] ProfileWriteRequest? request, HttpContext http,
        IVmRepository vms, IAuthorizationService authorization, ProfileSync sync, CancellationToken ct)
    {
        var (vm, failure) = await OwnVmAsync(http, name, vms, authorization, ct);
        if (vm is null) return failure!;
        if (request?.Profile is null) return CodedProblems.Validation("profile", "A body with the profile is required.");
        return Answer(await sync.SubmitAsync(vm.Owner, profile, request.Profile, request.BaseRevision, sync.VmOrigin(vm.Name), vm, ct));
    }

    private static async Task<IResult> DeleteFromVmAsync(string name, string profile, long? baseRevision, HttpContext http,
        IVmRepository vms, IAuthorizationService authorization, ProfileSync sync, CancellationToken ct)
    {
        var (vm, failure) = await OwnVmAsync(http, name, vms, authorization, ct);
        if (vm is null) return failure!;
        return Answer(await sync.SubmitAsync(vm.Owner, profile, null, baseRevision, sync.VmOrigin(vm.Name), vm, ct));
    }

    private static async Task<IResult> VmConflictsAsync(string name, HttpContext http, IVmRepository vms, IAuthorizationService authorization, ProfileSync sync, CancellationToken ct)
    {
        var (vm, failure) = await OwnVmAsync(http, name, vms, authorization, ct);
        if (vm is null) return failure!;
        if (sync.Replica(vm.Owner) is not { } replica) return TypedResults.Ok(new List<ProfileConflictResponse>());
        return TypedResults.Ok((await sync.ConflictsForAsync(replica, vm, ct)).Select(ToResponse).ToList());
    }

    private static async Task<IResult> ResolveFromVmAsync(string name, string id, [FromBody] ProfileResolveRequest? request, HttpContext http,
        IVmRepository vms, IAuthorizationService authorization, ProfileSync sync, CancellationToken ct)
    {
        var (vm, failure) = await OwnVmAsync(http, name, vms, authorization, ct);
        if (vm is null) return failure!;
        if (request?.Profile is null) return CodedProblems.Validation("profile", "A body with the resolved profile is required.");
        return Answer(await sync.ResolveAsync(vm.Owner, id, request.Profile, sync.VmOrigin(vm.Name), vm, ct));
    }

    // ---- device routes --------------------------------------------------------------------------

    /// <summary>Profiles are contents: only a device reads or writes them (as <c>GET /sync/records</c>).</summary>
    private static (string Account, string DeviceId)? Device(HttpContext http) =>
        http.User.FindFirst(DeviceAuthenticationHandler.DeviceClaim)?.Value is { Length: > 0 } device ? (http.User.NameOrEmpty(), device) : null;

    private static IResult DevicesOnly() => CodedProblems.Create(403, "devices-only", "Profiles are read and written by devices.");

    private static async Task<IResult> ListAsync(HttpContext http, ProfileSync sync, CancellationToken ct)
    {
        if (Device(http) is not { } device) return DevicesOnly();
        if (sync.Replica(device.Account) is not { } replica) return TypedResults.Ok(new List<ProfileSummary>());
        return TypedResults.Ok((await replica.RecordsAsync(RecordKinds.Profile, ct)).Where(r => !r.Deleted)
            .Select(r => new ProfileSummary(r.Id["profile:".Length..], r.Revision)).ToList());
    }

    private static async Task<IResult> GetAsync(string profile, HttpContext http, ProfileSync sync, CancellationToken ct)
    {
        if (Device(http) is not { } device) return DevicesOnly();
        if (sync.Replica(device.Account) is not { } replica || await replica.GetAsync(EngineReplica.ProfileId(profile), ct) is not { Deleted: false, Value: JsonObject value } record)
            return Problems.NotFound($"No profile named '{profile}'.");
        return TypedResults.Ok(new ProfileResponse(profile, record.Revision, (JsonObject)value.DeepClone()));
    }

    private static async Task<IResult> PutFromDeviceAsync(string profile, [FromBody] ProfileWriteRequest? request, HttpContext http, ProfileSync sync, CancellationToken ct)
    {
        if (Device(http) is not { } device) return DevicesOnly();
        if (request?.Profile is null) return CodedProblems.Validation("profile", "A body with the profile is required.");
        return Answer(await sync.SubmitAsync(device.Account, profile, request.Profile, request.BaseRevision, ProfileSync.DeviceOrigin(device.DeviceId), null, ct));
    }

    private static async Task<IResult> DeleteFromDeviceAsync(string profile, long? baseRevision, HttpContext http, ProfileSync sync, CancellationToken ct)
    {
        if (Device(http) is not { } device) return DevicesOnly();
        return Answer(await sync.SubmitAsync(device.Account, profile, null, baseRevision, ProfileSync.DeviceOrigin(device.DeviceId), null, ct));
    }

    private static async Task<IResult> DeviceConflictsAsync(HttpContext http, ProfileSync sync, CancellationToken ct)
    {
        if (Device(http) is not { } device) return DevicesOnly();
        if (sync.Replica(device.Account) is not { } replica) return TypedResults.Ok(new List<ProfileConflictResponse>());
        return TypedResults.Ok((await replica.ConflictsAsync(ct)).Select(ToResponse).ToList());
    }

    private static async Task<IResult> ResolveFromDeviceAsync(string id, [FromBody] ProfileResolveRequest? request, HttpContext http, ProfileSync sync, CancellationToken ct)
    {
        if (Device(http) is not { } device) return DevicesOnly();
        if (request?.Profile is null) return CodedProblems.Validation("profile", "A body with the resolved profile is required.");
        return Answer(await sync.ResolveAsync(device.Account, id, request.Profile, ProfileSync.DeviceOrigin(device.DeviceId), null, ct));
    }

    // ---- answers --------------------------------------------------------------------------------

    public static ProfileConflictResponse ToResponse(ConflictView conflict) => new(
        conflict.Id, conflict.ProfileId["profile:".Length..], conflict.ProfileRevision, conflict.Fields,
        conflict.Value["base"]?.DeepClone(), conflict.Value["ours"]?.DeepClone(), conflict.Value["theirs"]?.DeepClone(), conflict.Value["merged"]?.DeepClone());

    public static ProfileConflictNotice ToNotice(ConflictView conflict) => new(conflict.Id, conflict.ProfileId["profile:".Length..], conflict.Fields);

    private static IResult Answer(ProfileWriteOutcome outcome) => outcome switch
    {
        ProfileWriteOutcome.Written w => TypedResults.Ok(new ProfileWriteResponse(w.Queued ? "queued" : "written", w.Revision,
            w.File is null ? null : Encoding.UTF8.GetString(w.File), null, null)),
        ProfileWriteOutcome.Conflict c => TypedResults.Ok(new ProfileWriteResponse("conflict", null, null, c.ConflictId, c.Fields)),
        ProfileWriteOutcome.Refused r => CodedProblems.Create(r.Status, r.Code, r.Message),
        _ => throw new InvalidOperationException(),
    };

    private static IResult Answer(ProfileResolveOutcome outcome) => outcome switch
    {
        ProfileResolveOutcome.Resolved r => TypedResults.Ok(new ProfileWriteResponse("written", r.Revision, Encoding.UTF8.GetString(r.File), null, null)),
        ProfileResolveOutcome.AlreadyClosed => TypedResults.Ok(new ProfileWriteResponse("closed", null, null, null, null)),
        ProfileResolveOutcome.Recomputed c => TypedResults.Ok(new ProfileWriteResponse("recomputed", null, null, c.ConflictId, null)),
        ProfileResolveOutcome.Refused r => CodedProblems.Create(r.Status, r.Code, r.Message),
        _ => throw new InvalidOperationException(),
    };
}
