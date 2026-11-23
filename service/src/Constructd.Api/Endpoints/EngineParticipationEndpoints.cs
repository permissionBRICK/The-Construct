using System.Security.Cryptography;
using Construct.Keys;
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
/// key, device enrolment and revocation, the keys devices grant it (and their withdrawal where they do not belong),
/// and records read through it.
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

        api.MapPut("/device/ssh-key", PutSshKey)
            .RequireAuthorization(Policies.User).Audited("engine.device.ssh-key").WithName("PutDeviceSshKey").Produces(StatusCodes.Status204NoContent);

        api.MapPut("/device/backup-key", PutBackupKey)
            .RequireAuthorization(Policies.User).Audited("engine.device.backup-key").WithName("PutAccountBackupKey").Produces(StatusCodes.Status204NoContent);

        api.MapPost("/vms/{name}/backup-recipients", AddRecipientAsync)
            .RequireAuthorization(Policies.User).Audited("vm.backup-recipient.add").WithName("AddVmBackupRecipient").Produces<BackupRecipientResponse>();

        api.MapDelete("/vms/{name}/backup-recipients/{fingerprint}", RemoveRecipientAsync)
            .RequireAuthorization(Policies.User).Audited("vm.backup-recipient.remove").WithName("RemoveVmBackupRecipient").Produces(StatusCodes.Status204NoContent);

        api.MapPut("/engine/grants", PutGrants)
            .RequireAuthorization(Policies.User).Audited("engine.grants").WithName("PutEngineGrants").Produces(StatusCodes.Status204NoContent);

        api.MapDelete("/engine/grants", WithdrawGrants)
            .RequireAuthorization(Policies.User).Audited("engine.grants.withdraw").WithName("WithdrawEngineGrants").Produces(StatusCodes.Status204NoContent);

        api.MapGet("/engine/participation", (HttpContext http, EngineStores stores) =>
                TypedResults.Ok(new EngineParticipationResponse(stores.TakesPart(http.User.NameOrEmpty()))))
            .RequireAuthorization(Policies.User).WithName("GetEngineParticipation").Produces<EngineParticipationResponse>();

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

    /// <summary>Only a device registers, and only its own key: the device id is the caller's, never from the request.</summary>
    private static IResult PutSshKey([FromBody] DeviceSshKeyRequest request, HttpContext http, Constructd.Api.Devices.DeviceSshKeys deviceKeys)
    {
        if (http.User.FindFirst(DeviceAuthenticationHandler.DeviceClaim)?.Value is not { Length: > 0 } device)
            return Refused(403, "devices-only", "A device registers its key with its device token.");
        if (Constructd.Api.Devices.DeviceSshKeys.Normalize(request.PublicKey) is not { } key)
            return Refused(400, "invalid-key", "One OpenSSH public key line.");
        deviceKeys.Register(http.User.NameOrEmpty(), device, key);
        return TypedResults.NoContent();
    }

    /// <summary>A device registers its account's <c>B.pub</c> (public; <c>B</c>'s private half never comes here).</summary>
    private static IResult PutBackupKey([FromBody] AccountBackupKeyRequest request, HttpContext http, Constructd.Api.Devices.AccountBackupKeys backupKeys)
    {
        if (!http.User.HasClaim(c => c.Type == DeviceAuthenticationHandler.DeviceClaim))
            return Refused(403, "devices-only", "A device registers the account's backup key with its device token.");
        if (Constructd.Api.Devices.AccountBackupKeys.Normalize(request.PublicKey) is not { } pem)
            return Refused(400, "invalid-key", "One RSA public key of at least 3072 bits, as PEM.");
        backupKeys.Register(http.User.NameOrEmpty(), pem);
        return TypedResults.NoContent();
    }

    /// <summary>Enrols the account's backup key on one of the caller's own VMs.</summary>
    private static async Task<IResult> AddRecipientAsync(string name, HttpContext http, IVmRepository vms, Constructd.Api.Devices.AccountBackupKeys backupKeys, CancellationToken ct)
    {
        if (!http.User.HasClaim(c => c.Type == DeviceAuthenticationHandler.DeviceClaim)) return Refused(403, "devices-only", "A device asks with its device token.");
        if (await OwnVmAsync(name, http, vms, ct) is not { } vm) return Refused(404, "not-found", "No such VM of yours.");
        if (backupKeys.Of(vm.Owner) is not { } key) return Refused(409, "no-backup-key", "No device has registered the account's backup key here.");
        return await backupKeys.AddAsync(vm, ct) == 0
            ? TypedResults.Ok(new BackupRecipientResponse(key.Fingerprint))
            : Refused(502, "vm-unreachable", "The VM was not reached or refused the key.");
    }

    /// <summary>Removes another recipient from one of the caller's own VMs, only while the account's key is enrolled there.</summary>
    private static async Task<IResult> RemoveRecipientAsync(string name, string fingerprint, HttpContext http, IVmRepository vms,
        Constructd.Api.Devices.AccountBackupKeys backupKeys, CancellationToken ct)
    {
        if (!http.User.HasClaim(c => c.Type == DeviceAuthenticationHandler.DeviceClaim)) return Refused(403, "devices-only", "A device asks with its device token.");
        if (await OwnVmAsync(name, http, vms, ct) is not { } vm) return Refused(404, "not-found", "No such VM of yours.");
        if (!Constructd.Core.Logic.BackupJobKey.IsFingerprint(fingerprint)) return Refused(400, "invalid-fingerprint", "A fingerprint is 64 lowercase hex digits.");
        return await backupKeys.RemoveAsync(vm, fingerprint, ct) switch
        {
            Constructd.Api.Devices.AccountBackupKeys.RemoveOutcome.Removed => TypedResults.NoContent(),
            Constructd.Api.Devices.AccountBackupKeys.RemoveOutcome.NoAccountKey => Refused(409, "no-backup-key", "No device has registered the account's backup key here."),
            Constructd.Api.Devices.AccountBackupKeys.RemoveOutcome.AccountKey => Refused(409, "account-key", "The account's own backup key stays on its VMs."),
            Constructd.Api.Devices.AccountBackupKeys.RemoveOutcome.AccountKeyMissing => Refused(409, "account-key-not-enrolled",
                "The account's backup key is not enrolled on this VM; enrol it first, so the VM keeps a recipient a device holds."),
            _ => Refused(502, "vm-unreachable", "The VM was not reached or refused."),
        };
    }

    /// <summary>The VM when the caller is a device of its owner; null otherwise (no admin override: these are the owner's keys).</summary>
    private static async Task<Constructd.Core.Domain.Vm?> OwnVmAsync(string name, HttpContext http, IVmRepository vms, CancellationToken ct)
    {
        if (!http.User.HasClaim(c => c.Type == DeviceAuthenticationHandler.DeviceClaim)) return null;
        return await vms.GetAsync(name, ct) is { Deleting: false } vm && Constructd.Core.Logic.Ownership.SameName(vm.Owner, http.User.NameOrEmpty()) ? vm : null;
    }

    private static async Task<IResult> RevokeAsync(string id, HttpContext http, EngineDevices devices, Constructd.Api.Devices.DeviceSshKeys deviceKeys, CancellationToken ct)
    {
        try
        {
            await devices.RevokeAsync(http.User.NameOrEmpty(), id, ct);
        }
        catch (CryptographicException)
        {
            // The tokens are gone; without D the records are read once a device grants it.
        }

        // Its SSH key went with its tokens; the account's VMs lose it.
        deviceKeys.Changed(http.User.NameOrEmpty());

        return TypedResults.NoContent();
    }

    private static IResult PutGrants([FromBody] EngineGrantsRequest request, HttpContext http, EngineKeyStore keys, EngineStores stores)
    {
        if (request.Grants is not { Count: > 0 }) return Refused(400, "invalid-grant", "At least one grant.");
        // E12: the keys go only to an engine that takes part in the account's store; anywhere else the account keeps the old routes.
        if (!stores.TakesPart(http.User.NameOrEmpty()))
            return Refused(409, "not-participating", "This engine does not take part in the account's store; it keeps the account's sign-in and its own vault.");
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

    /// <summary>
    /// Withdraws the caller's grants from an engine that does not take part in the account's store (E12): <c>D</c> and
    /// <c>K</c> of every epoch and the replica profile sync kept against this engine's own store. The account's vault is
    /// the table again, as on a host of the old release. Records written to this engine's store for the account stay:
    /// without <c>D</c> nothing here opens them, and a secret a guest added while the keys were here has no other copy.
    /// Refused on an engine that takes part, so its keys stay; idempotent.
    /// </summary>
    private static IResult WithdrawGrants(HttpContext http, EngineKeyStore keys, EngineStores stores, Constructd.Api.Profiles.ProfileSync sync)
    {
        var account = http.User.NameOrEmpty();
        if (stores.TakesPart(account)) return Refused(409, "account-store", "This engine takes part in the account's store; its keys stay.");
        var granted = keys.GrantingAccounts(EngineGrants.Records).Contains(account, StringComparer.Ordinal)
            || keys.GrantingAccounts(EngineGrants.Vault).Contains(account, StringComparer.Ordinal);
        keys.DeleteGrant(account, EngineGrants.Records);
        keys.DeleteGrant(account, EngineGrants.Vault);
        sync.Drop(account);
        http.SetAuditDetail($"granted={(granted ? "yes" : "no")}");
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
