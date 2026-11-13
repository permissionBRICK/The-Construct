using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Repair;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Endpoints;

/// <summary>
/// The repair agent (design 11.5): read a VM's repair session, start one for the VM's latest failed
/// provision, reinstall or restore, reply to it, end it. The VM's owner and admins only, with user
/// credentials (a VM token cannot read or steer its own repair): the output can hold what the VM holds.
/// Changes are announced on <c>GET /events</c> as <c>repair.started</c>, <c>repair.output</c> and
/// <c>repair.ended</c> with the session's id and the VM.
/// </summary>
public static class RepairEndpoints
{
    public const int MaxReply = 16000;

    public static RouteGroupBuilder MapRepairEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/repair", LatestAsync).RequireAuthorization(Policies.User)
            .Produces<RepairSessionResponse>().WithName("GetVmRepair")
            .WithDescription("The VM's latest repair session with its lines after `after` (a line's `seq`); 404 `no-repair` when none ran since the engine started.");
        api.MapPost("/vms/{name}/repair", StartAsync).RequireAuthorization(Policies.User).Audited("vm.repair")
            .Produces<RepairSessionResponse>(StatusCodes.Status202Accepted).WithName("StartVmRepair")
            .WithDescription("Starts the repair agent on the VM's latest failed provision, reinstall or restore. 409: `no-failed-job`, `not-repairable`, `repair-running` (with `sessionId`), `operation-in-progress`, `vm-endpoint-unknown`.");
        api.MapGet("/vms/{name}/repair/{id}", GetAsync).RequireAuthorization(Policies.User)
            .Produces<RepairSessionResponse>().WithName("GetVmRepairSession");
        api.MapPost("/vms/{name}/repair/{id}/reply", ReplyAsync).RequireAuthorization(Policies.User)
            .Accepts<RepairReplyRequest>("application/json").Produces(StatusCodes.Status204NoContent).WithName("ReplyVmRepair")
            .WithDescription("Sends one message to the agent, which continues the same conversation. 409 `repair-ended` when the session is over.");
        api.MapDelete("/vms/{name}/repair/{id}", EndAsync).RequireAuthorization(Policies.User).Audited("vm.repair.end")
            .Produces(StatusCodes.Status204NoContent).WithName("EndVmRepair")
            .WithDescription("Ends the session and the agent's processes on the VM. Ending an ended session does nothing.");
        return api;
    }

    private static async Task<(Vm? Vm, IResult? Denied)> VmAsync(string name, HttpContext http, IVmRepository vms, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return (null, Problems.NotFound("Unknown VM."));
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return (null, denied);
        if (vm.Kind != VmKind.Primary) return (null, LifecycleEndpoints.Problem("not-a-primary"));
        return (vm, null);
    }

    private static async Task<IResult> LatestAsync(string name, long? after, HttpContext http, IVmRepository vms, RepairSessions repairs, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        return repairs.Latest(vm.Name) is { } session && session.VmCreated == vm.Created
            ? Results.Ok(RepairSessionResponse.From(session, after ?? 0))
            : CodedProblems.Create(404, "no-repair", "No repair ran on this VM since the engine started.");
    }

    private static async Task<IResult> GetAsync(string name, string id, long? after, HttpContext http, IVmRepository vms, RepairSessions repairs, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        return Find(repairs, vm, id) is { } session ? Results.Ok(RepairSessionResponse.From(session, after ?? 0)) : Problems.NotFound("Unknown repair session.");
    }

    private static async Task<IResult> StartAsync(string name, HttpContext http, IVmRepository vms, RepairSessions repairs, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        try
        {
            var failed = await repairs.LatestFailedAsync(vm, ct);
            var session = await repairs.StartAsync(vm, failed, automatic: false, ct);
            http.SetAuditDetail($"session={session.Id}; job={failed.Id}");
            return Results.Accepted($"/api/v1/vms/{vm.Name}/repair/{session.Id}", RepairSessionResponse.From(session, 0));
        }
        catch (RepairRefusedException ex)
        {
            return CodedProblems.Create(ex.Status, ex.Code, ex.Message, extra: ex.SessionId is null ? null : new() { ["sessionId"] = ex.SessionId });
        }
    }

    private static async Task<IResult> ReplyAsync(string name, string id, RepairReplyRequest request, HttpContext http, IVmRepository vms,
        RepairSessions repairs, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        if (request?.Text is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > MaxReply || text.Contains('\0'))
            return CodedProblems.Validation("text", $"Expected 1 to {MaxReply} characters.");
        if (Find(repairs, vm, id) is not { } session) return Problems.NotFound("Unknown repair session.");
        return await repairs.ReplyAsync(session, text, ct)
            ? Results.NoContent()
            : CodedProblems.Create(409, "repair-ended", "The repair session is over.");
    }

    private static async Task<IResult> EndAsync(string name, string id, HttpContext http, IVmRepository vms, RepairSessions repairs, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        if (Find(repairs, vm, id) is not { } session) return Problems.NotFound("Unknown repair session.");
        await repairs.EndAsync(session, "stopped");
        return Results.NoContent();
    }

    /// <summary>The session, only when it belongs to this VM (and not to an earlier VM of the same name).</summary>
    private static RepairSession? Find(RepairSessions repairs, Vm vm, string id) =>
        repairs.Find(vm.Name, id) is { } session && session.VmCreated == vm.Created ? session : null;
}
