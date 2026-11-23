using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Api.Endpoints;

/// <summary>
/// LAN access of a solo engine (<see cref="ILanAccess"/>): off after the install, switched on by the
/// device agent when the user pairs a second device. Only a solo install has the switch; a shared host's
/// installer opens its port to the network already.
/// </summary>
public static class LanAccessEndpoints
{
    public sealed record LanAccessRequest(bool? Enabled);

    public sealed record LanAccessResponse(bool Enabled);

    public static RouteGroupBuilder MapLanAccessEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/host/lan-access", GetAsync)
            .RequireAuthorization(Policies.Admin).WithName("GetLanAccess").Produces<LanAccessResponse>();
        api.MapPut("/host/lan-access", SetAsync)
            .RequireAuthorization(Policies.Admin).Audited("host.lan-access").WithName("SetLanAccess").Produces<LanAccessResponse>();
        return api;
    }

    private static async Task<IResult> GetAsync(ConstructdOptions options, IServiceProvider services, CancellationToken ct) =>
        Switch(options, services) is { } lan ? Results.Ok(new LanAccessResponse(await lan.GetAsync(ct))) : NotSolo();

    private static async Task<IResult> SetAsync(LanAccessRequest? request, HttpContext http, ConstructdOptions options, IServiceProvider services, CancellationToken ct)
    {
        if (Switch(options, services) is not { } lan) return NotSolo();
        if (request?.Enabled is not bool enabled) return CodedProblems.Validation("enabled", "'enabled' is required.");
        await lan.SetAsync(enabled, ct);
        http.SetAuditDetail(enabled ? "on" : "off");
        return Results.Ok(new LanAccessResponse(enabled));
    }

    private static ILanAccess? Switch(ConstructdOptions options, IServiceProvider services) =>
        options.Solo ? services.GetService<ILanAccess>() : null;

    private static IResult NotSolo() => Problems.Conflict("LAN access is switched only on a solo install's engine.");
}
