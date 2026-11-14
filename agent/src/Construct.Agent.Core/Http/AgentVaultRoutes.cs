using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
            await agent.DecideApprovalAsync(engine, id, decision.Decision == "allow", ct, decision.TtlSeconds);
            return TypedResults.NoContent();
        }).WithName("DecideVaultApproval").WithDescription("allow or deny; an allow of a lease request may carry ttlSeconds, the duration the user chose (60 to 86400). On a locked engine an allow first unlocks the VM that asked.");

        vault.MapDelete("/leases/{engine}/{id}", async (DeviceAgent agent, string engine, string id, CancellationToken ct) =>
        {
            await agent.EndLeaseAsync(engine, id, ct);
            return TypedResults.NoContent();
        }).WithName("EndVaultLease").WithDescription("Ends a lease before its time, at that engine; the VM's copy is scrubbed as on expiry.");

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

        api.MapPost("/engines/{engine}/vms/{vm}/backups/{backup}/restore", async (DeviceAgent agent, string engine, string vm, string backup,
                [FromBody] RestoreRequest? request, CancellationToken ct) =>
            TypedResults.Ok(await agent.RestoreBackupAsync(engine, vm, backup, request?.RequestId, ct)))
            .WithTags("agent").WithName("RestoreBackup")
            .WithDescription("Restores a stored backup into the running VM: the backup's key is unwrapped here with the account's backup key and goes only to the engine's restore job. Follow the job through the proxy.");

        // Jobs that clone project repos: the agent adds this device's git clone credentials (plan 7.4); the rest passes as sent.
        const string WithCredentials = " The agent adds secrets.gitCloneCredentialsB64 from this device's git credential helper, for the user's own VM only and"
            + " unless the body carries one; everything else goes to the engine as sent. The answer is the engine's (status and body), with"
            + " hostsWithoutCredential: the HTTP(S) hosts of the VM's repos that had no working credential here.";
        api.MapPost("/engines/{engine}/vms/{vm}/jobs", async (DeviceAgent agent, string engine, string vm, [FromBody] JsonObject body, CancellationToken ct) =>
                Answer(await agent.StartVmJobAsync(engine, vm, body, ct)))
            .WithTags("agent").WithName("StartVmJobWithCredentials").Produces<JobStartedWithCredentials>(StatusCodes.Status202Accepted)
            .WithDescription("The engine's POST /vms/{name}/jobs. Credentials go with a provision or a reinstall." + WithCredentials);
        api.MapPost("/engines/{engine}/vms", async (DeviceAgent agent, string engine, [FromBody] JsonObject body, CancellationToken ct) =>
                Answer(await agent.CreateVmAsync(engine, body, ct)))
            .WithTags("agent").WithName("CreateVmWithCredentials").Produces<JobStartedWithCredentials>(StatusCodes.Status202Accepted)
            .WithDescription("The engine's POST /vms. Credentials go with a create that has settings (its first provision)." + WithCredentials);
        api.MapPost("/engines/{engine}/jobs/{id}/retry", async (DeviceAgent agent, string engine, string id,
                [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonObject? body, CancellationToken ct) =>
                Answer(await agent.RetryJobAsync(engine, id, body, ct)))
            .WithTags("agent").WithName("RetryJobWithCredentials").Produces<JobStartedWithCredentials>(StatusCodes.Status202Accepted)
            .WithDescription("The engine's POST /jobs/{id}/retry. Credentials go with the retry of a provision or a reinstall." + WithCredentials);

        api.MapGet("/devices/{id}/secrets", async (DeviceAgent agent, string id, CancellationToken ct) => TypedResults.Ok(await agent.SecretsReadableByAsync(id, ct)))
            .WithTags("agent").WithName("SecretsReadableByDevice").WithDescription("Every secret a device could read (all of the account's), to rotate at their source when it is revoked.");
    }

    private static IResult Answer(EngineAnswer answer) => Results.Text(answer.Body, answer.ContentType, statusCode: answer.Status);
}
