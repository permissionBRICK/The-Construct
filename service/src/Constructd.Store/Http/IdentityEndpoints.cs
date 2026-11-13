using Construct.Keys;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Store.Http;

/// <summary>
/// The store's identity routes (8.1 to 8.5, 9.2): devices and their registration, engine
/// participants, pairing, recovery and the account's key wraps.
/// </summary>
public static partial class StoreEndpoints
{
    private const int MaxName = 100;
    private const int MaxAccount = 256;

    private static void MapIdentity(IEndpointRouteBuilder routes, Action<RouteHandlerBuilder, string> mutation)
    {
        // ---- devices ----

        mutation(routes.MapPost("/devices", (HttpContext http, StoreDirectory directory, [FromBody] EnrollRequest request) =>
        {
            if (!ParticipantPublicKey.TryParse(request.PublicKey, out var key)) return Error(400, "invalid-key", "Not a participant public key.");
            if (!ValidName(request.Name)) return Error(400, "invalid-name", "A device name is 1 to 100 characters.");
            if (!RequestProof.Verify(key!, ProofSubjects.Enroll(request.PublicKey), request.Proof, Now(http)))
                return Error(403, "invalid-proof", "The proof does not match the device key.");

            if (request.EnrollToken is { Length: > 0 } enrollToken)
            {
                return directory.EnrollRecovered(enrollToken, request.Name, key!) is { } recovered
                    ? Json(new EnrollResponse(recovered.Account, key!.Id, recovered.Token))
                    : Error(403, "invalid-enroll-token", "The enrolment token is unknown, used or expired, or this device is registered already.");
            }

            string account;
            if (request.SetupCode is { Length: > 0 })
            {
                if (request.Account is not { Length: > 0 and <= MaxAccount }) return Error(400, "invalid-account", "A first device names its account.");
                account = request.Account;
            }
            else if (http.RequestServices.GetService<IFirstDeviceAuthority>()?.AccountOf(http) is { } signedIn)
            {
                account = signedIn;
            }
            else
            {
                return Error(401, "unauthenticated", "A device is registered with a setup code, a recovery or by pairing.");
            }

            return directory.EnrollFirst(account, request.Name, key!, request.SetupCode is { Length: > 0 } ? request.SetupCode : null) is { } token
                ? Json(new EnrollResponse(account, key!.Id, token))
                : Error(409, "already-claimed", "This store or account has a device already; add devices by pairing.");
        }), "store.device.enroll");

        routes.MapGet("/devices", (HttpContext http, StoreDirectory directory) =>
            Principal(http) is { Kind: ParticipantKind.Device } principal ? Json(directory.Devices(principal.Account)) : DevicesOnly(http));

        mutation(routes.MapDelete("/devices/{id}", (HttpContext http, StoreDirectory directory, string id) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            return directory.Revoke(principal.Account, id) ? Results.NoContent() : Error(404, "not-found", "No such device.");
        }), "store.device.revoke");

        mutation(routes.MapPost("/devices/{id}/acknowledge", (HttpContext http, StoreDirectory directory, string id) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Engine } principal)
                return Principal(http) is null ? Unauthorized(http) : Error(403, "engines-only", "Only an engine acknowledges a revocation.");
            return directory.Acknowledge(principal.Account, id, principal.Id) ? Results.NoContent() : Error(409, "not-revoked", "That device is not revoked.");
        }), "store.device.acknowledge");

        // ---- engines ----

        mutation(routes.MapPost("/engines", (HttpContext http, StoreDirectory directory, [FromBody] EngineRegistration request) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            if (string.IsNullOrWhiteSpace(request.EngineId) || request.EngineId.Length > 128) return Error(400, "invalid-engine", "An engine id is 1 to 128 characters.");
            if (!ParticipantPublicKey.TryParse(request.HostPublicKey, out var hostKey)) return Error(400, "invalid-key", "Not a host public key.");
            return Json(new EngineRegistered(directory.RegisterEngine(principal.Account, request.EngineId, hostKey!)));
        }), "store.engine.register");

        // ---- key wraps (8.2) ----

        routes.MapGet("/keys", (HttpContext http, StoreDirectory directory) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            return directory.Keys(principal.Account) is { } keys ? Json(keys) : Error(404, "not-found", "This account has no keys yet.");
        });

        mutation(routes.MapPut("/keys", async (HttpContext http, StoreDirectory directory, RecordStore store, [FromBody] WrappedKeys keys, CancellationToken ct) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            if (keys.Epoch != await store.ActiveEpochAsync(principal.Account, ct)) return Error(409, "epoch-not-active", "The wraps are not of the active epoch.");
            return directory.PutKeys(principal.Account, keys) ? Results.NoContent() : Error(409, "keys-exist", "This account has its keys already.");
        }), "store.keys");

        // ---- recovery (8.5) ----

        mutation(routes.MapPut("/recovery", async (HttpContext http, StoreDirectory directory, RecordStore store, [FromBody] RecoverySetup setup, CancellationToken ct) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            if (setup.Verifier is not { Length: 32 } || setup.Wrap?.Sealed is not { Length: > 0 }) return Error(400, "invalid-recovery", "A verifier of 32 bytes and a wrap.");
            if (setup.Wrap.Epoch != await store.ActiveEpochAsync(principal.Account, ct)) return Error(409, "epoch-not-active", "The wrap is not of the active epoch.");
            directory.SetRecovery(principal.Account, setup.Verifier, setup.Wrap);
            return Results.NoContent();
        }), "store.recovery.set");

        mutation(routes.MapPost("/recovery", ([FromBody] RecoveryRequest request, StoreDirectory directory) =>
        {
            if (request.Account is not { Length: > 0 and <= MaxAccount } || request.Secret is not { Length: 32 })
                return Error(400, "invalid-recovery", "An account and a 32-byte secret.");
            return directory.Recover(request.Account, request.Secret) switch
            {
                RecoveryOutcome.Recovered recovered => Json(new RecoveryResponse(recovered.Wrap, recovered.EnrollToken)),
                RecoveryOutcome.RateLimited => Error(429, "rate-limited", "Too many recovery attempts for this account; try again later."),
                _ => Error(403, "wrong-recovery-secret", "The recovery code does not match."),
            };
        }), "store.recovery.use");

        // ---- pairing (8.3) ----

        mutation(routes.MapPost("/pairing", (StoreDirectory directory, [FromBody] PairingRequest request) =>
        {
            if (!ParticipantPublicKey.TryParse(request.PublicKey, out var key)) return Error(400, "invalid-key", "Not a participant public key.");
            if (!ValidName(request.Name)) return Error(400, "invalid-name", "A device name is 1 to 100 characters.");
            var (id, expires) = directory.CreatePairing(key!, request.Name);
            return Json(new PairingCreated(id, expires));
        }), "store.pairing.start");

        routes.MapGet("/pairing/{id}", (HttpContext http, StoreDirectory directory, string id) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device }) return DevicesOnly(http);
            return directory.Pairing(id) is { Account: null } pairing
                ? Json(new PairingView(pairing.Id, pairing.PublicKey.ToString(), pairing.Name, pairing.ExpiresAt))
                : Error(404, "not-found", "No such pairing, or it expired or was approved.");
        });

        mutation(routes.MapPut("/pairing/{id}", (HttpContext http, StoreDirectory directory, string id, [FromBody] PairingApproval approval) =>
        {
            if (Principal(http) is not { Kind: ParticipantKind.Device } principal) return DevicesOnly(http);
            if (directory.Pairing(id) is not { Account: null } pairing) return Error(404, "not-found", "No such pairing, or it expired or was approved.");
            if (approval.Grant is not { Length: > 0 } || approval.Signature is not { Length: > 0 }) return Error(400, "invalid-grant", "A grant and a signature.");
            var approver = directory.Devices(principal.Account).First(d => d.Id == principal.Id);
            if (!Pairing.Verify(ParticipantPublicKey.Parse(approver.PublicKey), id, pairing.PublicKey, approval.Grant, approval.Signature))
                return Error(400, "invalid-signature", "The signature is not the approver's over this pairing and grant.");
            return directory.Approve(id, principal.Account, principal.Id, approval.Grant, approval.Signature)
                ? Results.NoContent()
                : Error(404, "not-found", "No such pairing, or it expired or was approved.");
        }), "store.pairing.approve");

        mutation(routes.MapGet("/pairing/{id}/grant", (HttpContext http, StoreDirectory directory, string id) =>
        {
            if (directory.Pairing(id) is not { } pairing) return Error(410, "gone", "No such pairing, or it expired.");
            if (!RequestProof.Verify(pairing.PublicKey, ProofSubjects.Redeem(id), http.Request.Headers[RequestProof.Header].ToString(), Now(http)))
                return Error(403, "invalid-proof", "Only the device that started the pairing redeems it.");
            if (pairing.Redeemed) return Error(410, "gone", "The grant was redeemed already.");
            if (pairing.Account is null) return Error(409, "not-approved", "Not approved yet.");
            if (directory.Redeem(id) is not { } token) return Error(410, "gone", "The grant was redeemed already, or this device is registered already.");
            var approver = directory.Devices(pairing.Account).First(d => d.Id == pairing.ApproverId);
            return Json(new PairingRedeemed(pairing.Account, pairing.PublicKey.Id, token, pairing.Grant!, pairing.Signature!, approver.Id, approver.PublicKey));
        }), "store.pairing.redeem");
    }

    private static bool ValidName(string? name) => name is { Length: > 0 and <= MaxName } && !string.IsNullOrWhiteSpace(name);

    private static DateTimeOffset Now(HttpContext http) => (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
}
