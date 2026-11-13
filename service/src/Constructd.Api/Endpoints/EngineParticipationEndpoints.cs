using System.Security.Cryptography;
using Constructd.Api.Auth;
using Constructd.Api.Composition;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Sync.Sealing;
using Microsoft.AspNetCore.Mvc;

namespace Constructd.Api.Endpoints;

/// <summary>
/// This engine as a participant (docs/plans/construct-platform-design.md 8.1, 8.4, 8.7): its host
/// key, device enrolment and revocation, the keys devices grant it, and records read through it.
/// </summary>
public static class EngineParticipationEndpoints
{
    public static RouteGroupBuilder MapEngineParticipationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/engine", (EngineKeyStore keys) => TypedResults.Ok(new EngineIdentityResponse(keys.EngineId, keys.HostKey.PublicKey.ToString())))
            .RequireAuthorization(Policies.User).WithName("GetEngineIdentity").Produces<EngineIdentityResponse>();

        // Anonymous: the proof by a key that a device record of an enrolled user lists is the credential.
        api.MapPost("/devices/enroll", EnrollAsync)
            .AllowAnonymous().Audited("engine.device.enroll").WithName("EnrollDevice").Produces<DeviceEnrollResponse>();

        api.MapDelete("/devices/{id}", RevokeAsync)
            .RequireAuthorization(Policies.User).Audited("engine.device.revoke", "id").WithName("RevokeDevice").Produces(StatusCodes.Status204NoContent);

        api.MapPut("/engine/grants", PutGrants)
            .RequireAuthorization(Policies.User).Audited("engine.grants").WithName("PutEngineGrants").Produces(StatusCodes.Status204NoContent);

        api.MapGet("/sync/records/{id}", ReadRecordAsync)
            .RequireAuthorization(Policies.User).WithName("ReadSyncRecord").Produces<SyncRecordResponse>();

        return api;
    }

    private static async Task<IResult> EnrollAsync([FromBody] DeviceEnrollRequest request, EngineDevices devices, IUserStore users, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Account) || string.IsNullOrEmpty(request.DeviceId)) return Refused(400, "invalid-request", "An account and a device id.");
        if (await users.GetAsync(request.Account, ct) is not { Enabled: true }) return Refused(403, "not-enrolled", "This device cannot enrol here.");
        try
        {
            return await devices.EnrollAsync(request.Account, request.DeviceId, request.Proof, ct) is { } token
                ? TypedResults.Ok(new DeviceEnrollResponse(token))
                : Refused(403, "not-enrolled", "This device cannot enrol here.");
        }
        catch (CryptographicException)
        {
            return Refused(409, "no-record-key", "No device has granted this engine the record key yet.");
        }
    }

    private static async Task<IResult> RevokeAsync(string id, HttpContext http, EngineDevices devices, CancellationToken ct)
    {
        try
        {
            await devices.RevokeAsync(http.User.NameOrEmpty(), id, ct);
        }
        catch (CryptographicException)
        {
            // The tokens are gone; without D the records are read once a device grants it.
        }

        return TypedResults.NoContent();
    }

    private static IResult PutGrants([FromBody] EngineGrantsRequest request, HttpContext http, EngineKeyStore keys)
    {
        if (request.Grants is not { Count: > 0 }) return Refused(400, "invalid-grant", "At least one grant.");
        try
        {
            foreach (var grant in request.Grants) keys.PutGrant(http.User.NameOrEmpty(), grant);
        }
        catch (ArgumentException)
        {
            return Refused(400, "not-grantable", "An engine is granted D and K only.");
        }
        catch (CryptographicException)
        {
            return Refused(400, "invalid-grant", "A grant does not open under this engine's host key.");
        }

        return TypedResults.NoContent();
    }

    /// <summary>Only a device gets record contents; a user signed in without a device token does not.</summary>
    private static async Task<IResult> ReadRecordAsync(string id, HttpContext http, RecordStore store, EngineStoreIdentity identity, IRecordKeyProvider keys, CancellationToken ct)
    {
        if (!http.User.HasClaim(c => c.Type == DeviceAuthenticationHandler.DeviceClaim)) return Refused(403, "devices-only", "Records are read by devices.");
        var account = http.User.NameOrEmpty();
        if (await identity.Client(store, account).GetAsync(id, ct) is not { Deleted: false, Ciphertext: not null } record) return Refused(404, "not-found", "No such record.");
        try
        {
            var payload = RecordSealer.Open(await keys.GetKeyAsync(account, record.Epoch, ct), record.Envelope, record.Ciphertext);
            return TypedResults.Ok(new SyncRecordResponse(record.Id, record.Kind, record.Actor, record.Revision, RecordPayload.ValueOf(payload)?.DeepClone()));
        }
        catch (CryptographicException)
        {
            return Refused(409, "no-record-key", "This engine cannot open that record.");
        }
    }

    private static IResult Refused(int status, string code, string message) =>
        Results.Json(new { code, message }, StoreJson.Options, statusCode: status);
}
