using System.Text.Json.Nodes;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Endpoints;

/// <summary>
/// What the old client hands the engine when its user moves over. Adopting a VM the old client made on this PC
/// (<see cref="LocalVmAdoption"/>), as a job the device agent starts; the adopting admin becomes the VM's owner. And the
/// old client's root key for a VM of a host that ran the old stack and updated itself in place (<see cref="OldClientKeys"/>),
/// which only the VM's owner hands over. The agent reads the old client's files itself and sends their content, so the
/// engine never opens a path a caller names; the key is never logged, audited or answered.
/// </summary>
public static class LocalAdoptionEndpoints
{
    /// <param name="Settings">The old client's <c>.construct-settings.json</c>, as JSON.</param>
    /// <param name="InstanceState">The named instance's <c>instances\&lt;name&gt;.json</c>; omitted for the default instance.</param>
    /// <param name="SshPrivateKey">The old client's private key for <c>root</c> on the VM.</param>
    public sealed record AdoptLocalVmRequest(JsonObject? Settings, JsonObject? InstanceState, string? SshPrivateKey);

    /// <param name="SshPrivateKey">The old client's private key for <c>root</c> on the VM (OpenSSH private key text).</param>
    /// <param name="Settings">The old client's <c>.construct-settings.json</c>: the VM's settings when the engine stores none.</param>
    /// <param name="InstanceState">The named instance's <c>instances\&lt;name&gt;.json</c>; omitted for the default instance.</param>
    public sealed record OldClientKeyRequest(string? SshPrivateKey, JsonObject? Settings, JsonObject? InstanceState);

    /// <param name="Status"><c>stored</c> (the key reaches the VM and is kept now), <c>kept</c> (the engine keeps this key
    /// already) or <c>reachable</c> (the engine reaches the VM without it; nothing was stored).</param>
    public sealed record OldClientKeyResponse(string Status);

    public static RouteGroupBuilder MapLocalAdoptionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/adopt", AdoptAsync)
            .RequireAuthorization(Policies.Admin).Audited("vm.adopt").WithName("AdoptLocalVm")
            .Produces<JobStartAnswer>(StatusCodes.Status202Accepted);
        api.MapPut("/vms/{name}/old-client-key", HandOverKeyAsync)
            .RequireAuthorization(Policies.User).Audited("vm.old-client-key").WithName("HandOverOldClientKey")
            .Accepts<OldClientKeyRequest>("application/json").Produces<OldClientKeyResponse>();
        return api;
    }

    /// <summary>The VM's owner only (not an admin for someone else's VM, not a VM token): the key gives root on the VM.</summary>
    private static async Task<IResult> HandOverKeyAsync(string name, OldClientKeyRequest? body, HttpContext http, IVmRepository vms,
        IServiceProvider services, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (!http.User.IsKnownUser() || !Constructd.Core.Logic.Ownership.SameName(vm.Owner, http.User.NameOrEmpty()))
            return LifecycleEndpoints.Problem("not-owner", 403);
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        if (vm.Deleting) return LifecycleEndpoints.Problem("vm-deleting");
        if (!OldClientKeys.ValidKey(body?.SshPrivateKey)) return CodedProblems.Validation("sshPrivateKey", "Expected the old client's OpenSSH private key.");
        try
        {
            var outcome = await OldClientKeys.HandOverAsync(services, vm, body!.SshPrivateKey!, body.Settings, body.InstanceState, http.User.Actor(), ct);
            http.SetAuditDetail("status=" + outcome.Status);
            return Results.Ok(new OldClientKeyResponse(outcome.Status));
        }
        catch (OldClientKeys.Refusal refused)
        {
            http.SetAuditDetail("refused=" + refused.Code);
            return CodedProblems.Create(refused.Status, refused.Code, refused.Message);
        }
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
