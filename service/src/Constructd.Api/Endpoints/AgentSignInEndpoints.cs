using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.SignIn;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Signing in to a VM's coding agents (design 11.8, S34): start the agent's own sign-in on the VM, read what its
/// terminal shows, type into it, end it. One runs per VM; starting another ends it. The VM's owner only, with user
/// credentials: not an admin of the host, not a VM token, since the account signed in is the owner's. The terminal's
/// text and the input never reach a log, the audit log or the store. Changes are announced on <c>GET /events</c>, to the
/// owner only, as <c>agent-sign-in.started</c>, <c>agent-sign-in.output</c> and <c>agent-sign-in.ended</c> with the
/// session's id and the VM.
/// </summary>
public static class AgentSignInEndpoints
{
    public static RouteGroupBuilder MapAgentSignInEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/agents/{agent}/sign-in", StartAsync).RequireAuthorization(Policies.User).Audited("vm.agent.sign-in")
            .Produces<AgentSignInResponse>(StatusCodes.Status202Accepted).WithName("StartAgentSignIn")
            .WithDescription("Starts the agent's own sign-in on the VM in a terminal (claude-code, codex, opencode, antigravity); a sign-in running on the VM ends. 409: `vm-endpoint-unknown`, `sign-in-not-started` (the connection to the VM failed).");
        api.MapGet("/vms/{name}/agents/{agent}/sign-in", GetAsync).RequireAuthorization(Policies.User)
            .Produces<AgentSignInResponse>().WithName("GetAgentSignIn")
            .WithDescription("The agent's latest sign-in on the VM; 404 `no-sign-in` when none ran since the engine started or another agent's came after it.");
        // Only refusals are audited: input is typing, and its text is never recorded.
        api.MapPost("/vms/{name}/agents/{agent}/sign-in/input", InputAsync).RequireAuthorization(Policies.User)
            .Audited("vm.agent.sign-in.input", auditSuccess: false)
            .Accepts<AgentSignInInputRequest>("application/json").Produces(StatusCodes.Status204NoContent).WithName("AgentSignInInput")
            .WithDescription("Types into the sign-in's terminal. 409 `sign-in-ended` when it is over.");
        api.MapDelete("/vms/{name}/agents/{agent}/sign-in", EndAsync).RequireAuthorization(Policies.User).Audited("vm.agent.sign-in.end")
            .Produces(StatusCodes.Status204NoContent).WithName("EndAgentSignIn")
            .WithDescription("Ends the sign-in and its processes on the VM. Ending an ended one does nothing.");
        return api;
    }

    private static async Task<(Vm? Vm, IResult? Denied)> VmAsync(string name, string agent, HttpContext http, IVmRepository vms, CancellationToken ct)
    {
        if (!CodingAgents.Known.Contains(agent)) return (null, CodedProblems.Validation("agent", "Expected " + string.Join(", ", CodingAgents.Known) + "."));
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return (null, Problems.NotFound("Unknown VM."));
        if (!http.User.IsKnownUser() || !Ownership.SameName(http.User.NameOrEmpty(), vm.Owner)) return (null, LifecycleEndpoints.Problem("not-owner", 403));
        if (vm.Kind != VmKind.Primary) return (null, LifecycleEndpoints.Problem("not-a-primary"));
        if (vm.Deleting) return (null, LifecycleEndpoints.Problem("vm-deleting"));
        return (vm, null);
    }

    /// <summary>The VM's latest sign-in, when it is this agent's and belongs to this VM (not an earlier one of the name).</summary>
    private static AgentSignInSession? Find(AgentSignIns signIns, Vm vm, string agent) =>
        signIns.Latest(vm.Name) is { } s && s.VmCreated == vm.Created && s.Agent == agent ? s : null;

    private static IResult None() => CodedProblems.Create(404, "no-sign-in", "No sign-in of this agent runs or ran on this VM since the engine started.");

    private static async Task<IResult> StartAsync(string name, string agent, HttpContext http, IVmRepository vms, AgentSignIns signIns, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, agent, http, vms, ct);
        if (vm is null) return denied!;
        try
        {
            var session = await signIns.StartAsync(vm, agent, ct);
            http.SetAuditDetail($"agent={agent}; session={session.Id}");
            return Results.Accepted($"/api/v1/vms/{vm.Name}/agents/{agent}/sign-in", AgentSignInResponse.From(session));
        }
        catch (AgentSignInRefusedException ex)
        {
            return CodedProblems.Create(409, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> GetAsync(string name, string agent, HttpContext http, IVmRepository vms, AgentSignIns signIns, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, agent, http, vms, ct);
        if (vm is null) return denied!;
        return Find(signIns, vm, agent) is { } session ? Results.Ok(AgentSignInResponse.From(session)) : None();
    }

    private static async Task<IResult> InputAsync(string name, string agent, AgentSignInInputRequest? request, HttpContext http, IVmRepository vms,
        AgentSignIns signIns, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, agent, http, vms, ct);
        if (vm is null) return denied!;
        if (request is null || (request.Text is null) == (request.Key is null))
            return CodedProblems.Validation("text", "Expected text or key.");
        if (request.Text is { } text && !AgentSignInScript.ValidInput(text))
            return CodedProblems.Validation("text", $"Expected at most {AgentSignInScript.MaxInput} characters without control characters.");
        if (request.Key is { } key && !AgentSignInScript.Keys.Contains(key))
            return CodedProblems.Validation("key", "Expected " + string.Join(" or ", AgentSignInScript.Keys) + ".");
        if (Find(signIns, vm, agent) is not { } session) return None();
        return await signIns.InputAsync(session, request.Text, request.Key, ct)
            ? Results.NoContent()
            : CodedProblems.Create(409, "sign-in-ended", "The sign-in is over.");
    }

    private static async Task<IResult> EndAsync(string name, string agent, HttpContext http, IVmRepository vms, AgentSignIns signIns, CancellationToken ct)
    {
        var (vm, denied) = await VmAsync(name, agent, http, vms, ct);
        if (vm is null) return denied!;
        if (Find(signIns, vm, agent) is not { } session) return None();
        http.SetAuditDetail($"agent={agent}; session={session.Id}");
        await signIns.EndAsync(session, "stopped");
        return Results.NoContent();
    }
}
