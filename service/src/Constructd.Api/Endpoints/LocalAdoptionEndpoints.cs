using System.Text.Json.Nodes;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Adopting a VM the old client made on this PC (<see cref="LocalVmAdoption"/>), as a job the device
/// agent starts. The agent reads the old client's files itself and sends their content, so the engine
/// never opens a path a caller names. The adopting admin becomes the VM's owner.
/// </summary>
public static class LocalAdoptionEndpoints
{
    /// <param name="Settings">The old client's <c>.construct-settings.json</c>, as JSON.</param>
    /// <param name="InstanceState">The named instance's <c>instances\&lt;name&gt;.json</c>; omitted for the default instance.</param>
    /// <param name="SshPrivateKey">The old client's private key for <c>root</c> on the VM.</param>
    public sealed record AdoptLocalVmRequest(JsonObject? Settings, JsonObject? InstanceState, string? SshPrivateKey);

    public static RouteGroupBuilder MapLocalAdoptionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/adopt", AdoptAsync)
            .RequireAuthorization(Policies.Admin).Audited("vm.adopt").WithName("AdoptLocalVm")
            .Produces<JobStartAnswer>(StatusCodes.Status202Accepted);
        return api;
    }

    private static async Task<IResult> AdoptAsync(string name, AdoptLocalVmRequest? body, HttpContext http, IServiceProvider services,
        IServiceScopeFactory scopes, IJobEngine jobs, CancellationToken ct)
    {
        http.SetAuditTarget(name);
        var owner = http.User.Actor();
        var request = new LocalAdoptionRequest(name, owner, body?.Settings, body?.InstanceState, body?.SshPrivateKey ?? "");
        try
        {
            await LocalVmAdoption.ValidateAsync(services, request, ct);
        }
        catch (VmAdoptionException ex)
        {
            return ex.Code == "invalid" ? CodedProblems.Validation("request", ex.Message) : Problems.Conflict(ex.Message);
        }

        var job = await jobs.SubmitAsync(LocalVmAdoption.JobKind, name, owner, async (progress, token) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            return new JobOutcome(await LocalVmAdoption.AdoptAsync(scope.ServiceProvider, request, owner, progress, token));
        }, ct);
        return Results.Accepted($"/api/v1/jobs/{job.Id}", new JobStartAnswer(job.Id));
    }
}
