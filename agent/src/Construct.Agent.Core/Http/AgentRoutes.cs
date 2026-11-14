using System.Text.Json;
using Construct.Agent.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Construct.Agent.Http;

/// <summary>The agent's own routes under <c>/api</c>; the engine proxy is <see cref="EngineProxy"/>.</summary>
public static class AgentRoutes
{
    public static void Map(WebApplication app, AgentHost host, DateTimeOffset startedAt)
    {
        var api = app.MapGroup("/api").WithTags("agent");

        api.MapGet("/health", () => TypedResults.Ok(new AgentHealth("ok", host.Options.Version, Environment.ProcessId, startedAt)))
            .WithName("Health").WithDescription("Answers without the token: the repair command and the shell ask whether the agent runs.");

        api.MapGet("/state", (DeviceAgent agent) => TypedResults.Ok(new AgentStateView(agent.Device(), agent.Engines(), agent.Pairing())))
            .WithName("GetState");

        api.MapPost("/shutdown", () =>
        {
            app.Lifetime.StopApplication();
            return TypedResults.NoContent();
        }).WithName("Shutdown");

        api.MapGet("/events", EventsAsync).WithName("Events").Produces(200, contentType: "text/event-stream")
            .WithDescription("Server-sent events: `agent`, `pairing`, `job.progress`, `job.phase`, `job.state`, `job.lost`; `reset` when the events after `since` are gone. Each `data` is an AgentEvent.");

        // ---- engines ----

        api.MapGet("/engines", (DeviceAgent agent) => TypedResults.Ok(agent.Engines())).WithName("ListEngines");

        api.MapPost("/engines/probe", async (DeviceAgent agent, [FromBody] ProbeRequest request, CancellationToken ct) =>
            TypedResults.Ok(await agent.ProbeAsync(request.Address, ct))).WithName("ProbeEngine")
            .WithDescription("Connects to a pasted address without any credential and returns the certificate pin for the user to confirm.");

        api.MapGet("/engines/local", async Task<IResult> (DeviceAgent agent, CancellationToken ct) =>
            await agent.ProbeLocalAsync(ct) is { } probe ? TypedResults.Ok(probe) : TypedResults.NoContent())
            .WithName("ProbeLocalEngine").Produces<EngineProbe>().Produces(204)
            .WithDescription("The solo engine on this machine on loopback; 204 when none answers.");

        api.MapPost("/engines", async (DeviceAgent agent, [FromBody] AddEngineRequest request, CancellationToken ct) =>
            TypedResults.Ok(request.WindowsSignIn
                ? await agent.AddEngineWithWindowsAsync(request.Address, request.Pin, request.Name, request.UserToken, ct)
                : await agent.AddEngineAsync(request.Address, request.Pin, request.Name, request.UserToken, ct))).WithName("AddEngine")
            .WithDescription("Adds an engine with the pin the user confirmed. The user's sign-in there is userToken, or, with windowsSignIn, the signed-in Windows user where the engine offers Negotiate (Windows only; 501 not-available elsewhere).");

        api.MapDelete("/engines/{id}", async (DeviceAgent agent, string id, CancellationToken ct) =>
        {
            await agent.RemoveEngineAsync(id, ct);
            return TypedResults.NoContent();
        }).WithName("RemoveEngine");

        api.MapPost("/engines/{id}/connect", async (DeviceAgent agent, string id, [FromBody] ConnectEngineRequest request, CancellationToken ct) =>
            TypedResults.Ok(await agent.ConnectEngineAsync(id, request.VaultMode, ct))).WithName("ConnectEngine")
            .WithDescription("Grants the engine D (and K when vaultMode is available) and enrols this device there.");

        EngineProxy.Map(api);

        // ---- this device (8.2, 8.5) ----

        api.MapPost("/device/setup", async (DeviceAgent agent, [FromBody] SetupRequest request, CancellationToken ct) =>
            TypedResults.Ok(new RecoveryCodeResponse(await agent.SetupAsync(request.Engine, request.DeviceName, request.VaultMode, ct)))).WithName("SetUpFirstDevice")
            .WithDescription("The account's first device, against the engine's built-in store. The answer carries the recovery code, the only time it is shown.");

        api.MapPost("/device/recovery-code", async (DeviceAgent agent, CancellationToken ct) =>
            TypedResults.Ok(new RecoveryCodeResponse(await agent.NewRecoveryCodeAsync(ct)))).WithName("NewRecoveryCode")
            .WithDescription("Issues a new recovery code; the old one stops working. Also how the first run or the upgrade shows the code again before it was confirmed.");

        api.MapPost("/device/recovery-code/kept", (DeviceAgent agent) =>
        {
            agent.ConfirmRecoveryCode();
            return TypedResults.NoContent();
        }).WithName("ConfirmRecoveryCode")
            .WithDescription("The user confirmed the recovery code shown last was kept: the setup's recovery step is done.");

        api.MapPost("/device/recover", async (DeviceAgent agent, [FromBody] RecoverRequest request, CancellationToken ct) =>
        {
            await agent.RecoverAsync(request.Engine, request.Account, request.RecoveryCode, request.DeviceName, ct);
            return TypedResults.Ok(agent.Device());
        }).WithName("Recover");

        // ---- pairing (8.3) ----

        api.MapPost("/pairing", async (DeviceAgent agent, [FromBody] StartPairingRequest request, CancellationToken ct) =>
            TypedResults.Ok(await agent.StartPairingAsync(request.Engine, request.DeviceName, ct))).WithName("StartPairing")
            .WithDescription("This device asks to join an account; the agent waits for the approval by itself and reports on /api/events.");

        api.MapGet("/pairing", Results<Ok<PairingView>, NotFound> (DeviceAgent agent) =>
            agent.Pairing() is { } view ? TypedResults.Ok(view) : TypedResults.NotFound()).WithName("GetPairing");

        api.MapDelete("/pairing", (DeviceAgent agent) =>
        {
            agent.CancelPairing();
            return TypedResults.NoContent();
        }).WithName("CancelPairing");

        api.MapGet("/pairing/requests/{id}", async (DeviceAgent agent, string id, CancellationToken ct) =>
            TypedResults.Ok(await agent.PairingRequestAsync(id, ct))).WithName("GetPairingRequest");

        api.MapPost("/pairing/requests/{id}/approve", async (DeviceAgent agent, string id, [FromBody] ApprovePairingRequest request, CancellationToken ct) =>
        {
            await agent.ApprovePairingAsync(id, request.PublicKey, ct);
            return TypedResults.NoContent();
        }).WithName("ApprovePairing")
            .WithDescription("publicKey is the one GetPairingRequest returned with the words the user compared.");

        // ---- devices (8.4) ----

        api.MapGet("/devices", async (DeviceAgent agent, CancellationToken ct) => TypedResults.Ok(await agent.DevicesAsync(ct))).WithName("ListDevices");

        api.MapDelete("/devices/{id}", async (DeviceAgent agent, string id, CancellationToken ct) =>
            TypedResults.Ok(await agent.RevokeDeviceAsync(id, ct))).WithName("RevokeDevice");

        // ---- the vault (8.2, 8.4, 9.1, 10.3) ----

        AgentVaultRoutes.Map(api);
    }

    private static async Task EventsAsync(HttpContext context, DeviceAgent agent, long? since)
    {
        var from = since ?? (long.TryParse(context.Request.Headers["Last-Event-ID"].ToString(), out var last) ? last : 0);
        using var subscription = agent.Events.Subscribe(from);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        var ct = context.RequestAborted;
        await context.Response.WriteAsync(": connected\n\n", ct);
        if (subscription.Gap) await context.Response.WriteAsync($"event: reset\ndata: {{\"seq\":{subscription.Current}}}\n\n", ct);
        foreach (var item in subscription.Replay) await WriteAsync(context, item, ct);
        await context.Response.Body.FlushAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var keepalive = CancellationTokenSource.CreateLinkedTokenSource(ct);
                keepalive.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await subscription.Live.WaitToReadAsync(keepalive.Token)) break;
                    while (subscription.Live.TryRead(out var item)) await WriteAsync(context, item, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": keepalive\n\n", ct);
                }

                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static Task WriteAsync(HttpContext context, AgentEvent item, CancellationToken ct) =>
        context.Response.WriteAsync($"id: {item.Seq}\nevent: {item.Type}\ndata: {JsonSerializer.Serialize(item, AgentJson.Options)}\n\n", ct);
}

public sealed record AgentHealth(string Status, string Version, int Pid, DateTimeOffset StartedAt);

public sealed record AgentStateView(DeviceView Device, IReadOnlyList<EngineView> Engines, PairingView? Pairing);

public sealed record ProbeRequest(string Address);

/// <param name="Pin">The pin ProbeEngine returned and the user confirmed.</param>
/// <param name="UserToken">The user's sign-in at the engine (an API token), when this device has no device token there.</param>
/// <param name="WindowsSignIn">Sign in as the Windows user instead of with <paramref name="UserToken"/>.</param>
public sealed record AddEngineRequest(string Address, string Pin, string? Name = null, [property: ConstructSecret] string? UserToken = null, bool WindowsSignIn = false);

public sealed record ConnectEngineRequest(string? VaultMode = null);

public sealed record SetupRequest(string Engine, string DeviceName, string? VaultMode = null);

public sealed record RecoverRequest(string Engine, string Account, [property: ConstructSecret] string RecoveryCode, string DeviceName);

public sealed record RecoveryCodeResponse([property: ConstructSecret] string RecoveryCode);

public sealed record StartPairingRequest(string Engine, string DeviceName);

public sealed record ApprovePairingRequest(string PublicKey);
