using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Construct.Agent.Http;

/// <summary>
/// The vault's device side under <c>/api/vault</c> (plan 8.2, 8.4, 9.1, 10.3). The agent decrypts with <c>K</c>;
/// the UI gets names, descriptions, access and provenance, a value only from <c>reveal</c>, and never <c>K</c>.
/// </summary>
public static class AgentVaultRoutes
{
    public static void Map(RouteGroupBuilder api)
    {
        var vault = api.MapGroup("/vault").WithTags("vault");

        vault.MapGet("/secrets", async (DeviceAgent agent, CancellationToken ct) => TypedResults.Ok(await agent.ListSecretsAsync(ct)))
            .WithName("ListSecrets").WithDescription("Names, descriptions, the sealed auto-approve flag, leases on every engine reached and who wrote each; never a value.");

        vault.MapPost("/secrets", async (DeviceAgent agent, [FromBody] SecretInput input, CancellationToken ct) => TypedResults.Ok(await agent.AddSecretAsync(input, ct)))
            .WithName("AddSecret").WithDescription("A new secret under a free name; 409 exists otherwise.");

        vault.MapPatch("/secrets/{name}", async (DeviceAgent agent, string name, [FromBody] SecretChange change, CancellationToken ct) =>
            TypedResults.Ok(await agent.ChangeSecretAsync(name, change, ct)))
            .WithName("ChangeSecret").WithDescription("Replaces the description, username or value; null keeps a field. A new value ends the leases on the old one at every engine.");

        vault.MapDelete("/secrets/{name}", async (DeviceAgent agent, string name, CancellationToken ct) =>
        {
            await agent.DeleteSecretAsync(name, ct);
            return TypedResults.NoContent();
        }).WithName("DeleteSecret");

        vault.MapPost("/secrets/{name}/reveal", async (DeviceAgent agent, string name, CancellationToken ct) => TypedResults.Ok(await agent.RevealSecretAsync(name, ct)))
            .WithName("RevealSecret").WithDescription("The one route that returns a value, for the user who asked to see it.");

        vault.MapPut("/secrets/{name}/auto-approve", async (DeviceAgent agent, string name, [FromBody] AutoApproveRequest request, CancellationToken ct) =>
        {
            await agent.SetAutoApproveAsync(name, request.On, ct);
            return TypedResults.NoContent();
        }).WithName("SetSecretAutoApprove");

        vault.MapGet("/history", async (DeviceAgent agent, CancellationToken ct) => TypedResults.Ok(await agent.SecretHistoryAsync(ct)))
            .WithName("SecretHistory").WithDescription("Every write of every secret, newest first, with its provenance (a VM, the approving or entering device, the request id).");

        vault.MapGet("/approvals", async (DeviceAgent agent, CancellationToken ct) => TypedResults.Ok(await agent.ApprovalsAsync(ct)))
            .WithName("ListVaultApprovals");

        vault.MapPost("/approvals/{engine}/{id}", async (DeviceAgent agent, string engine, string id, [FromBody] ApprovalDecision decision, CancellationToken ct) =>
        {
            if (decision.Decision is not ("allow" or "deny")) throw new AgentFailure(400, "invalid-decision", "allow or deny.");
            await agent.DecideApprovalAsync(engine, id, decision.Decision == "allow", ct);
            return TypedResults.NoContent();
        }).WithName("DecideVaultApproval").WithDescription("allow or deny. On a locked engine an allow first unlocks the VM that asked.");

        vault.MapGet("/entry-requests", async (DeviceAgent agent, CancellationToken ct) => TypedResults.Ok(await agent.EntryRequestsAsync(ct)))
            .WithName("ListEntryRequests").WithDescription("Agents asking the user to enter a secret (construct secret ask). Description and reason are the agent's text.");

        vault.MapPost("/entry-requests/{engine}/{id}", async (DeviceAgent agent, string engine, string id, [FromBody] EntryInput input, CancellationToken ct) =>
        {
            await agent.EnterSecretAsync(engine, id, input, ct);
            return TypedResults.NoContent();
        }).WithName("EnterSecret").WithDescription("Creates the secret only while its name is free (409 exists, nothing overwritten); it needs approval for every use and grants no lease.");

        vault.MapPost("/entry-requests/{engine}/{id}/decline", async (DeviceAgent agent, string engine, string id, CancellationToken ct) =>
        {
            await agent.DeclineEntryAsync(engine, id, ct);
            return TypedResults.NoContent();
        }).WithName("DeclineEntryRequest");

        vault.MapGet("/engines/{engine}/mode", async (DeviceAgent agent, string engine, CancellationToken ct) => TypedResults.Ok(await agent.VaultModeAsync(engine, ct)))
            .WithName("GetVaultMode");

        vault.MapPut("/engines/{engine}/mode", async (DeviceAgent agent, string engine, [FromBody] VaultModeRequest request, CancellationToken ct) =>
            TypedResults.Ok(await agent.SetVaultModeAsync(engine, request.Mode, ct)))
            .WithName("SetVaultMode").WithDescription("available grants the engine K; locked withdraws it (VMs a device unlocked keep it until they stop).");

        vault.MapPost("/engines/{engine}/vms/{vm}/unlock", async (DeviceAgent agent, string engine, string vm, CancellationToken ct) =>
        {
            await agent.UnlockVmAsync(engine, vm, ct);
            return TypedResults.NoContent();
        }).WithName("UnlockVmVault").WithDescription("Gives a running VM on a locked engine K until it stops.");

        api.MapGet("/devices/{id}/secrets", async (DeviceAgent agent, string id, CancellationToken ct) => TypedResults.Ok(await agent.SecretsReadableByAsync(id, ct)))
            .WithTags("agent").WithName("SecretsReadableByDevice").WithDescription("Every secret a device could read (all of the account's), to rotate at their source when it is revoked.");
    }
}
