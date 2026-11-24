using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Targeted coding-agent updates without a full reprovision: <c>POST /vms/{name}/agents/{agent}/update</c> updates one
/// coding agent and <c>POST /vms/{name}/agents/update</c> updates all reported agents that are behind (or the named
/// subset) in one job. The VM's owner only, primaries only, audited, holds the VM's operation gate, and starts a
/// stopped VM first (design 12.1).
/// </summary>
public static class AgentUpdateEndpoints
{
    public static RouteGroupBuilder MapAgentUpdateEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/agents/{agent}/update", UpdateOneAsync)
            .RequireAuthorization(Policies.User)
            .Audited("vm.agent.update")
            .Produces<VmJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .WithName("UpdateVmAgent")
            .WithDescription("Updates one coding agent on the VM (claude, codex, opencode, antigravity, t3) in a dedicated job without reprovisioning the VM. Starts the VM first when it is off. The VM's owner only.");
        api.MapPost("/vms/{name}/agents/update", UpdateManyAsync)
            .RequireAuthorization(Policies.User)
            .Audited("vm.agents.update")
            .Accepts<UpdateVmAgentsRequest>("application/json")
            .Produces<VmJobAcceptedResponse>(StatusCodes.Status202Accepted)
            .WithName("UpdateVmAgents")
            .WithDescription("Updates coding agents on the VM in one dedicated job without reprovisioning the VM. Names specific agents in `agents`, or omits it to update every reported agent that has a newer release. Starts the VM first when it is off. The VM's owner only.");
        return api;
    }

    private static async Task<(Vm? Vm, IResult? Denied)> OwnerPrimaryAsync(string name, HttpContext http, IVmRepository vms, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return (null, Problems.NotFound("Unknown VM."));
        if (!http.User.IsKnownUser() || !Ownership.SameName(http.User.NameOrEmpty(), vm.Owner))
            return (null, LifecycleEndpoints.Problem("not-owner", 403));
        if (vm.Kind != VmKind.Primary) return (null, LifecycleEndpoints.Problem("not-a-primary"));
        if (vm.Deleting) return (null, LifecycleEndpoints.Problem("vm-deleting"));
        return (vm, null);
    }

    private static async Task<IResult> UpdateOneAsync(string name, string agent, HttpContext http, IVmRepository vms,
        AgentUpdateJobs updates, CancellationToken ct)
    {
        if (!AgentUpdateScript.IsKnownTool(agent))
            return CodedProblems.Validation("agent", "Expected " + string.Join(", ", AgentUpdateScript.KnownTools) + ".");
        var (vm, denied) = await OwnerPrimaryAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        return await SubmitAsync(vm, http, updates, [agent], ct);
    }

    private static async Task<IResult> UpdateManyAsync(string name, UpdateVmAgentsRequest? request, HttpContext http, IVmRepository vms,
        AgentUpdateJobs updates, CancellationToken ct)
    {
        IReadOnlyList<string> tools;
        if (request?.Agents is { } requested)
        {
            if (requested.Count == 0 || requested.Any(a => !AgentUpdateScript.IsKnownTool(a)) ||
                requested.Distinct(StringComparer.Ordinal).Count() != requested.Count)
                return CodedProblems.Validation("agents", "Expected one or more unique agents: " + string.Join(", ", AgentUpdateScript.KnownTools) + ".");
            tools = requested;
        }
        else
        {
            tools = [];
        }

        var (vm, denied) = await OwnerPrimaryAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        if (request?.Agents is null) tools = await updates.BehindAsync(vm, ct);
        return await SubmitAsync(vm, http, updates, tools, ct);
    }

    private static async Task<IResult> SubmitAsync(Vm vm, HttpContext http, AgentUpdateJobs updates, IReadOnlyList<string> tools, CancellationToken ct)
    {
        try
        {
            var job = await updates.StartAsync(vm, http.User.Actor(), tools, ct);
            http.SetAuditDetail($"agents={string.Join(',', AgentUpdateScript.Order(tools))}; job={job.Id}");
            return Results.Accepted("/api/v1/jobs/" + job.Id, new VmJobAcceptedResponse(job.Id, job.Kind, false));
        }
        catch (ProvisionRequestException ex)
        {
            if (ex.Code == "maintenance") return await MaintenanceFilter.RefusedAsync(http);
            return CodedProblems.Create(ex.Status, ex.Code, ex.Message, extra: ex.JobId is null ? null : new() { ["jobId"] = ex.JobId });
        }
    }
}
