using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;

namespace Constructd.Api.Endpoints;

/// <summary>Request to move a VM's virtual hard disk to <paramref name="DestinationPath"/> (null/empty uses the host default).</summary>
public sealed record MoveVmStorageRequest(string? DestinationPath = null);

/// <summary>Request to move all VMs on this host to <paramref name="DestinationPath"/> (null/empty uses the host default).</summary>
public sealed record MoveAllVmStorageRequest(string? DestinationPath = null);

/// <summary>Answer to <c>POST /host/vms/storage/move</c>: which VMs were queued and the resolved destination folder.</summary>
public sealed record MoveAllVmStorageResponse(IReadOnlyList<string> Vms, string DestinationPath);

/// <summary>
/// Admin routes for relocating VM virtual hard disks on Hyper-V hosts: one VM at a time, or every VM on the host
/// sequentially (typically triggered when an admin changes the host default storage path).
/// </summary>
public static class VmStorageEndpoints
{
    public static RouteGroupBuilder MapVmStorageEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/storage/move", MoveOneAsync)
            .RequireAuthorization(Policies.Admin).Audited("vm.storage.move")
            .Accepts<MoveVmStorageRequest>("application/json")
            .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted)
            .WithName("MoveVmStorage");

        api.MapPost("/host/vms/storage/move", MoveAllAsync)
            .RequireAuthorization(Policies.Admin).Audited("vm.storage.move.all")
            .Accepts<MoveAllVmStorageRequest>("application/json")
            .Produces<MoveAllVmStorageResponse>(StatusCodes.Status202Accepted)
            .WithName("MoveAllVmStorage");

        return api;
    }

    private static async Task<IResult> MoveOneAsync(string name, MoveVmStorageRequest? request, HttpContext http,
        IVmRepository vms, VmStorageMoveJobs mover, CancellationToken ct)
    {
        http.SetAuditTarget(name);
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        try
        {
            var job = await mover.SubmitAsync(vm, request?.DestinationPath, http.User.Actor(), ct);
            return Results.Accepted("/api/v1/jobs/" + job.Id, new JobAcceptedResponse(job.Id));
        }
        catch (StorageMoveRequestException ex)
        {
            return ToProblem(http, ex);
        }
    }

    private static async Task<IResult> MoveAllAsync(MoveAllVmStorageRequest? request, HttpContext http,
        VmStorageMoveJobs mover, CancellationToken ct)
    {
        http.SetAuditTarget("host");
        try
        {
            var (queued, dest) = await mover.StartAllAsync(request?.DestinationPath, http.User.Actor(), ct);
            http.SetAuditDetail($"count={queued.Count}, path={dest}");
            return Results.Accepted((string?)null, new MoveAllVmStorageResponse(queued, dest));
        }
        catch (StorageMoveRequestException ex)
        {
            return ToProblem(http, ex);
        }
    }

    private static IResult ToProblem(HttpContext http, StorageMoveRequestException ex) => ex.Code switch
    {
        "validation" => CodedProblems.Validation("destinationPath", ex.Message),
        "maintenance" => MaintenanceFilter.RefusedAsync(http).GetAwaiter().GetResult(),
        _ => CodedProblems.Create(ex.Status, ex.Code, ex.Message),
    };
}
