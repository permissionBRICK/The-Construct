using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Endpoints;

public static class TokenUsageEndpoints
{
    public static RouteGroupBuilder MapTokenUsageEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/usage", PostAsync)
            .RequireAuthorization(Policies.VmScoped)
            .Audited("vm.usage", auditSuccess: false).WithName("PostTokenUsage");
        api.MapGet("/host/usage", HostAsync).RequireAuthorization(Policies.User).WithName("GetHostTokenUsage").Produces<TokenUsageSummary>();
        api.MapGet("/vms/{name}/usage", VmAsync).RequireAuthorization(Policies.User).WithName("GetVmTokenUsage").Produces<TokenUsageSummary>();
        api.MapGet("/vms/{name}/agents", AgentsAsync).RequireAuthorization(Policies.User).WithName("GetVmAgents").Produces<VmAgentsResponse>();
        return api;
    }

    private static async Task<IResult> HostAsync(string? window, HttpContext http, ITokenUsageStore store, IVmRepository vms, IClock clock, CancellationToken ct)
    {
        window ??= "today";
        if (!TokenUsageMath.ValidWindow(window)) return Problems.BadRequest("window must be today, month or all.");
        var rows = await store.ListAsync(http.User.IsAdmin() ? null : http.User.NameOrEmpty(), null, ct);
        return TypedResults.Ok(TokenUsageMath.Aggregate(rows, window, clock.UtcNow,
            await vms.ListAsync(http.User.IsAdmin() ? null : http.User.NameOrEmpty(), ct)));
    }

    private static async Task<IResult> VmAsync(string name, string? window, HttpContext http, IVmRepository repository,
        IAuthorizationService authorization, ITokenUsageStore store, IClock clock, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name, Policies.VmOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!;
        window ??= "today";
        if (!TokenUsageMath.ValidWindow(window)) return Problems.BadRequest("window must be today, month or all.");
        return TypedResults.Ok(TokenUsageMath.Aggregate(await store.ListAsync(http.User.IsAdmin() ? null : http.User.NameOrEmpty(), lookup.Vm!.Name, ct), window, clock.UtcNow, [lookup.Vm]));
    }

    private static async Task<IResult> AgentsAsync(string name, HttpContext http, IVmRepository repository, IAuthorizationService authorization,
        VmAgents agents, IAgentReleases releases, IVmDesiredStateSource desired, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name, Policies.VmOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!;
        var report = await agents.GetAsync(lookup.Vm!, ct);
        var channel = (await desired.GetDesiredForAsync(lookup.Vm!, ct))?.Desired.Settings.T3CodeChannel;
        var list = new List<VmAgentResponse>();
        foreach (var agent in report?.Agents ?? [])
        {
            var latest = await releases.LatestAsync(agent.Tool, agent.Tool == "t3" ? channel : null, ct);
            list.Add(new(agent.Tool, VmAgents.Names[agent.Tool], agent.Version, latest, VmAgents.IsNewer(latest, agent.Version)));
        }
        return TypedResults.Ok(new VmAgentsResponse(report?.ReportedAt, list));
    }

    private static bool ValidCost(decimal? cost) => cost is >= 0 and <= 9223372036854.775807m;
    private static async Task<IResult> PostAsync(string name, TokenUsageRequest? request, HttpContext http,
        IVmRepository repository, IAuthorizationService authorization, ITokenUsageStore store, IClock clock, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, repository, authorization, name, Policies.VmSelfOrOwnerOrAdmin, ct);
        if (!lookup.Ok) return lookup.Failure!;
        if (ApiHelpers.FenceDeleting(lookup.Vm!) is { } fenced)
        {
            http.SetAuditDetail("vm is being deleted");
            return fenced;
        }
        if (request?.GeneratedAt is null || request.Days is null || request.Days.Count > 200)
            return Problems.BadRequest("generatedAt and days are required; at most 200 rows are allowed.");
        var days = new List<TokenUsageDay>();
        var keys = new HashSet<(string, string)>();
        foreach (var row in request.Days)
        {
            if (row is null || !TokenUsageMath.ValidDay(row.Day) || row.Tool is not ("claude" or "codex" or "opencode") ||
                !keys.Add((row.Day!, row.Tool)) || row.InputTokens is not >= 0 || row.OutputTokens is not >= 0 ||
                row.CacheCreateTokens is not >= 0 || row.CacheReadTokens is not >= 0 || row.TotalTokens is not >= 0 || !ValidCost(row.CostUsd))
                return Problems.BadRequest("Each row requires a unique calendar day/month and known tool, non-negative integer token counts and a representable non-negative cost.");
            if (row.Models?.Count > 64 || row.Models?.Any(m => string.IsNullOrWhiteSpace(m.Key) || m.Key.Length > 128 ||
                    m.Value?.TotalTokens is not >= 0 || !ValidCost(m.Value.CostUsd)) == true)
                return Problems.BadRequest("At most 64 named models per row, with non-negative token counts and costs, are allowed.");
            days.Add(new(row.Day!, row.Tool, row.InputTokens.Value, row.OutputTokens.Value, row.CacheCreateTokens.Value,
                row.CacheReadTokens.Value, row.TotalTokens.Value, checked((long)decimal.Truncate(row.CostUsd!.Value * 1_000_000m)),
                JsonSerializer.Serialize(row.Models ?? new Dictionary<string, TokenUsageModelRequest?>(), ApiJson.Options)));
        }
        var agents = new List<VmAgentVersion>();
        foreach (var agent in request.Agents ?? [])
        {
            if (agent?.Tool is not { } tool || !VmAgents.Names.ContainsKey(tool) || agent.Version is not { } version ||
                !VmAgents.VersionPattern().IsMatch(version) || agents.Any(a => a.Tool == tool))
                return Problems.BadRequest("Each agent requires a unique known tool (claude, codex, opencode, t3) and a version.");
            agents.Add(new(tool, version));
        }
        if (!await store.UpsertAsync(lookup.Vm!, days, clock.UtcNow, ct)) return LifecycleEndpoints.Problem("power-state-changed");
        if (request.Agents is not null) await http.RequestServices.GetRequiredService<VmAgents>().SaveAsync(lookup.Vm!, agents, clock.UtcNow, ct);
        http.SetAuditDetail($"rows={days.Count}");
        return TypedResults.NoContent();
    }
}
