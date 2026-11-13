using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Endpoints;

/// <summary>
/// The hosted key vault (docs/plans/key-vault-hosted.md). Three credentials, three surfaces:
/// <list type="bullet">
/// <item>the user (the Companion) manages their OWN vault — an admin gets no access to anybody else's;</item>
/// <item>a paired device (<c>VaultDevice</c>) answers approvals and file decisions, nothing else;</item>
/// <item>a VM token makes requests and runs scrubs for its own primary VM; children get no vault.</item>
/// </list>
/// Values, keys and tokens never enter an audit detail or a log line.
/// </summary>
public static class VaultEndpoints
{
    private const int MaxGuestBody = VaultProtocol.MaxRequestBytes, MaxScrubBody = 4 * 1024 * 1024, MaxWaitSeconds = 25;

    public static RouteGroupBuilder MapVaultEndpoints(this RouteGroupBuilder api)
    {
        // ---- the user (owner only) ----------------------------------------------------------
        api.MapGet("/vault/entries", GetEntriesAsync).RequireAuthorization(Policies.User).WithName("GetVaultEntries").Produces<VaultEntriesResponse>();
        api.MapPut("/vault/entries", PutEntriesAsync).RequireAuthorization(Policies.User).Audited("vault.sync").WithName("PutVaultEntries").Produces<VaultPutEntriesResponse>();
        api.MapPut("/vault/settings", PutSettingsAsync).RequireAuthorization(Policies.User).Audited("vault.settings").WithName("PutVaultSettings").Produces(StatusCodes.Status204NoContent);
        api.MapPost("/vms/{name}/vault/unlock", UnlockAsync).RequireAuthorization(Policies.User).Audited("vault.unlock").WithName("UnlockVault").Produces(StatusCodes.Status204NoContent);
        api.MapGet("/vault/leases", GetLeasesAsync).RequireAuthorization(Policies.User).WithName("GetVaultLeases").Produces<VaultLeasesResponse>();
        api.MapDelete("/vault/leases/{id}", RevokeLeaseAsync).RequireAuthorization(Policies.User).Audited("vault.lease.revoke", "id").WithName("RevokeVaultLease").Produces(StatusCodes.Status204NoContent);
        api.MapGet("/vault/devices", GetDevicesAsync).RequireAuthorization(Policies.User).WithName("GetVaultDevices").Produces<VaultDevicesResponse>();
        api.MapPost("/vault/devices", PairDeviceAsync).RequireAuthorization(Policies.User).Audited("vault.device.pair").WithName("PairVaultDevice").Produces<VaultDevicePairedResponse>();
        api.MapDelete("/vault/devices/{id}", RevokeDeviceAsync).RequireAuthorization(Policies.User).Audited("vault.device.revoke", "id").WithName("RevokeVaultDevice").Produces(StatusCodes.Status204NoContent);
        api.MapGet("/vault/activity", GetActivityAsync).RequireAuthorization(Policies.User).WithName("GetVaultActivity").Produces<VaultActivityResponse>();

        // ---- a vault kept as secret records (plan 8.2, 9.1, 10.3) ----------------------------
        api.MapGet("/vault/mode", GetModeAsync).RequireAuthorization(Policies.User).WithName("GetVaultMode").Produces<VaultModeResponse>();
        api.MapPut("/vault/mode", PutModeAsync).RequireAuthorization(Policies.User).Audited("vault.mode").WithName("PutVaultMode").Produces(StatusCodes.Status204NoContent);
        api.MapGet("/vault/entry-requests", GetEntryRequests).RequireAuthorization(Policies.User).WithName("GetVaultEntryRequests").Produces<VaultEntryRequestsResponse>();
        api.MapPost("/vault/entry-requests/{id}", SaveEntryAsync).RequireAuthorization(Policies.User).Audited("vault.entry.save", "id").WithName("SaveVaultEntryRequest").Produces(StatusCodes.Status204NoContent);
        api.MapPost("/vault/entry-requests/{id}/decline", DeclineEntryAsync).RequireAuthorization(Policies.User).Audited("vault.entry.decline", "id").WithName("DeclineVaultEntryRequest").Produces(StatusCodes.Status204NoContent);

        // ---- the user or a paired device -----------------------------------------------------
        api.MapGet("/vault/approvals", GetApprovals).RequireAuthorization(Policies.VaultApprover).WithName("GetVaultApprovals").Produces<VaultApprovalsResponse>();
        api.MapPost("/vault/approvals/{id}", DecideAsync).RequireAuthorization(Policies.VaultApprover).Audited("vault.approval", "id").WithName("DecideVaultApproval").Produces(StatusCodes.Status204NoContent);
        api.MapGet("/vault/files", GetFilesAsync).RequireAuthorization(Policies.VaultApprover).WithName("GetVaultFiles").Produces<VaultFilesResponse>();
        api.MapPost("/vault/files/{id}", DecideFileAsync).RequireAuthorization(Policies.VaultApprover).Audited("vault.file", "id").WithName("DecideVaultFile").Produces(StatusCodes.Status204NoContent);
        api.MapGet("/vault/device", WhoAmI).RequireAuthorization(Policies.VaultDevice).WithName("GetVaultDevice");

        // ---- the guest (its own primary VM's token) -----------------------------------------
        api.MapPost("/vms/{name}/vault/requests", SubmitAsync).RequireAuthorization(Policies.VmToken).Audited("vault.request").WithName("SubmitVaultRequest");
        api.MapGet("/vms/{name}/vault/requests/{id}", PollAsync).RequireAuthorization(Policies.VmToken).WithName("PollVaultRequest");
        api.MapGet("/vms/{name}/vault/scrubs", GetScrubsAsync).RequireAuthorization(Policies.VmToken).WithName("GetVaultScrubs");
        api.MapPost("/vms/{name}/vault/scrubs/{id}", PostScrubAsync).RequireAuthorization(Policies.VmToken).Audited("vault.scrub").WithName("PostVaultScrub");
        return api;
    }

    public sealed record EntryDto(string? Name, string? Description, bool? HasUsername, [property: ConstructSecret] string? Payload, long? UpdatedAt, string? UpdatedBy, bool? Deleted, bool? AutoApprove = null);
    public sealed record EntriesRequest(List<EntryDto?>? Entries);
    public sealed record SettingsRequest(string? Mode, [property: ConstructSecret] string? Key, string? KeyCheck);
    /// <param name="Key">The Companion's vault: <c>K</c> in base64.</param>
    /// <param name="Grant">A vault kept as records: <c>K</c> sealed by a device to this engine's host key.</param>
    public sealed record UnlockRequest([property: ConstructSecret] string? Key, KeyGrantDto? Grant = null);
    /// <summary><c>K</c> sealed to this engine's host key (Construct.Keys.EngineGrants, name <c>K</c>).</summary>
    public sealed record KeyGrantDto(long Epoch, [property: ConstructSecret] byte[]? Sealed);
    public sealed record ModeRequest(string? Mode, KeyGrantDto? Grant);
    /// <param name="Payload">The value sealed with <c>K</c> under the entry's name and <paramref name="UpdatedAt"/>.</param>
    public sealed record EntrySaveRequest([property: ConstructSecret] string? Payload, long? UpdatedAt, bool? HasUsername);
    public sealed record DecisionRequest(string? Decision);
    public sealed record FileRequest(string? Action);
    public sealed record DeviceRequest(string? Label);

    private static string Owner(HttpContext http) => http.User.VaultDeviceOwner() ?? http.User.NameOrEmpty();

    private static IResult Problem(VaultProblemException e) =>
        e.Field is { } field ? CodedProblems.Validation(field, e.Message) : CodedProblems.Create(e.Status, e.Code, e.Message);

    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (VaultProblemException e)
        {
            http.AppendAuditDetail("code=" + e.Code);
            return Problem(e);
        }
    }

    // ── the user ────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetEntriesAsync(HttpContext http, VaultHostService vault, CancellationToken ct)
    {
        var view = await vault.GetEntriesAsync(Owner(http), ct).ConfigureAwait(false);
        return Results.Ok(new VaultEntriesResponse(view.Revision, view.Mode, view.KeyCheck, view.UnlockedVms,
            [.. view.Entries.Select(e => new VaultEntryResponse(e.Name, e.Description, e.HasUsername, e.Payload,
                e.UpdatedAt, e.UpdatedBy, e.Deleted, e.AutoApprove))],
            [.. view.Devices.Select(VaultDeviceResponse.From)]));
    }

    private static Task<IResult> PutEntriesAsync(EntriesRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        http.SetAuditTarget(Owner(http));
        if (request?.Entries is not { } list) return CodedProblems.Validation("entries", "Expected {entries:[…]}.");
        var entries = list.Select((e, i) => e is null
            ? throw new VaultProblemException(400, "validation", "Expected an entry object.", $"entries[{i}]")
            : new VaultEntry(e.Name ?? "", e.Description ?? "", e.HasUsername ?? false, e.Payload, e.UpdatedAt ?? 0, e.UpdatedBy ?? "", e.Deleted ?? false, e.AutoApprove ?? false)).ToArray();
        var (revision, applied) = await vault.PutEntriesAsync(Owner(http), entries, ct).ConfigureAwait(false);
        http.SetAuditDetail($"entries={entries.Length}, applied={applied}, revision={revision}");
        return Results.Ok(new VaultPutEntriesResponse(revision, applied));
    });

    private static Task<IResult> PutSettingsAsync(SettingsRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        http.SetAuditTarget(Owner(http));
        if (request is null) return CodedProblems.Validation("mode", "Expected {mode, key?, keyCheck?}.");
        http.SetAuditDetail($"mode={request.Mode ?? "-"}, key={(request.Key is null ? "absent" : "present")}");
        await vault.PutSettingsAsync(Owner(http), request.Mode, request.Key, request.KeyCheck, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static Task<IResult> UnlockAsync(string name, UnlockRequest? request, HttpContext http, IVmRepository vms, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        if (!VmNameValidator.IsValid(name)) return Problems.BadRequest($"VM name is invalid: {VmNameValidator.Rule}");
        var vm = await vms.GetAsync(name, ct).ConfigureAwait(false);
        if (vm is null) return Problems.NotFound($"No VM named '{name}'.");
        // The vault is the owner's alone: an admin cannot unlock another user's VM.
        if (!Ownership.SameName(vm.Owner, http.User.NameOrEmpty())) return CodedProblems.Create(403, "not-owner", "Only the VM's owner can unlock its key vault.");
        await vault.UnlockAsync(vm, request?.Key, ct, Grant(request?.Grant)).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static async Task<IResult> GetLeasesAsync(HttpContext http, VaultHostService vault, CancellationToken ct) =>
        Results.Ok(new VaultLeasesResponse([.. (await vault.LeasesAsync(Owner(http), ct).ConfigureAwait(false)).Select(l =>
            new VaultLeaseResponse(l.Id, l.Vm, l.Name, l.UsesLeft, l.ExpiresAt.ToUnixTimeMilliseconds(), l.Reason, l.Origin))]));

    private static Task<IResult> RevokeLeaseAsync(string id, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        await vault.RevokeLeaseAsync(Owner(http), id, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    // The approval page's address (Logic.VaultWebAddress): the host setting vault.webUrl, the service option,
    // or the service's own address. webUrlSource tells the Companion which, so it can point at the setting
    // while phones still get the self-signed default.
    private static async Task<(string Url, string Source)> WebAddressAsync(IHostConfigStore config, ConstructdOptions options, CancellationToken ct) =>
        VaultWebAddress.Resolve((await config.GetAsync<VaultConfig>("vault", ct).ConfigureAwait(false))?.WebUrl, options);

    private static async Task<IResult> GetDevicesAsync(HttpContext http, VaultHostService vault, IHostConfigStore config, ConstructdOptions options, CancellationToken ct)
    {
        var (webUrl, webUrlSource) = await WebAddressAsync(config, options, ct).ConfigureAwait(false);
        return Results.Ok(new VaultDevicesResponse(webUrl, webUrlSource, [.. (await vault.DevicesAsync(Owner(http), ct).ConfigureAwait(false)).Select(VaultDeviceResponse.From)]));
    }

    private static Task<IResult> PairDeviceAsync(DeviceRequest? request, HttpContext http, VaultHostService vault, IHostConfigStore config, ConstructdOptions options, CancellationToken ct) => Run(http, async () =>
    {
        http.SetAuditTarget(Owner(http));
        var (device, token) = await vault.PairDeviceAsync(Owner(http), request?.Label, ct).ConfigureAwait(false);
        http.SetAuditDetail($"device={device.Id}");
        var (webUrl, webUrlSource) = await WebAddressAsync(config, options, ct).ConfigureAwait(false);
        return Results.Ok(new VaultDevicePairedResponse(device.Id, token, webUrl, webUrlSource));
    });

    private static Task<IResult> RevokeDeviceAsync(string id, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        await vault.RevokeDeviceAsync(Owner(http), id, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static async Task<IResult> GetActivityAsync(HttpContext http, VaultHostService vault, CancellationToken ct) =>
        Results.Ok(new VaultActivityResponse([.. (await vault.ActivityAsync(Owner(http), ct).ConfigureAwait(false))
            .Select(e => new VaultActivityEvent(e.At.ToUnixTimeMilliseconds(), e.Vm, e.Text, e.Warning))]));

    // ── a vault kept as records ─────────────────────────────────────────────────

    private static VaultKeyGrant? Grant(KeyGrantDto? grant) => grant?.Sealed is { Length: > 0 } sealedKey ? new VaultKeyGrant(grant.Epoch, sealedKey) : null;

    // device:<id> for a device of the new stack, phone:<id> for a paired approval device, user:<name> for a sign-in.
    private static string Approver(HttpContext http) =>
        http.User.FindFirst(DeviceAuthenticationHandler.DeviceClaim)?.Value is { } device ? "device:" + device
        : http.User.FindFirst(ConstructdClaims.VaultDevice)?.Value is { } phone ? "phone:" + phone
        : "user:" + http.User.NameOrEmpty();

    private static async Task<IResult> GetModeAsync(HttpContext http, VaultHostService vault, CancellationToken ct)
    {
        var view = await vault.ModeAsync(Owner(http), ct).ConfigureAwait(false);
        return Results.Ok(new VaultModeResponse(view.Mode, view.HoldsKey, view.Records, view.UnlockedVms));
    }

    private static Task<IResult> PutModeAsync(ModeRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        http.SetAuditTarget(Owner(http));
        http.SetAuditDetail($"mode={VaultProtocol.Sanitize(request?.Mode, 20)}, grant={(request?.Grant is null ? "absent" : "present")}");
        await vault.SetModeAsync(Owner(http), request?.Mode, Grant(request?.Grant), ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static IResult GetEntryRequests(HttpContext http, VaultHostService vault) =>
        Results.Ok(new VaultEntryRequestsResponse(vault.EntryRequests(Owner(http))));

    // Only a device holds K, so only a device enters a value; it is that write's provenance (9.1 row 4).
    private static Task<IResult> SaveEntryAsync(string id, EntrySaveRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        if (http.User.FindFirst(DeviceAuthenticationHandler.DeviceClaim)?.Value is not { } device)
            return CodedProblems.Create(403, "devices-only", "Only a device enters a secret.");
        http.SetAuditDetail($"device={device}");
        await vault.SaveEntryAsync(Owner(http), id, "device:" + device, request?.Payload, request?.UpdatedAt ?? 0, request?.HasUsername ?? false, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static Task<IResult> DeclineEntryAsync(string id, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        await vault.DeclineEntryAsync(Owner(http), id, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    // ── the user or a paired device ─────────────────────────────────────────────

    private static IResult GetApprovals(HttpContext http, VaultHostService vault) =>
        Results.Ok(new VaultApprovalsResponse(vault.Approvals(Owner(http))));

    private static Task<IResult> DecideAsync(string id, DecisionRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        if (request?.Decision is not ("approve" or "deny")) return CodedProblems.Validation("decision", "Expected approve or deny.");
        http.SetAuditDetail($"decision={request.Decision}, by={(http.User.IsVaultDevice() ? "device" : "user")}");
        await vault.DecideAsync(Owner(http), id, request.Decision == "approve", ct, Approver(http)).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static async Task<IResult> GetFilesAsync(HttpContext http, VaultHostService vault, CancellationToken ct) =>
        Results.Ok(new VaultFilesResponse(await vault.FilesAsync(Owner(http), ct).ConfigureAwait(false)));

    private static Task<IResult> DecideFileAsync(string id, FileRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        http.SetAuditDetail($"action={request?.Action ?? "-"}, by={(http.User.IsVaultDevice() ? "device" : "user")}");
        await vault.DecideFileAsync(Owner(http), id, request?.Action, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static IResult WhoAmI(HttpContext http) => Results.Ok(new
    {
        user = http.User.VaultDeviceOwner(), label = http.User.FindFirst(ConstructdClaims.VaultDeviceLabel)?.Value ?? "",
    });

    // ── the guest ───────────────────────────────────────────────────────────────

    // The token's own primary VM only. A child has no vault: its route answers vault-child.
    private static async Task<(Vm? Vm, IResult? Failure)> GuestVmAsync(string name, HttpContext http, IVmRepository vms, CancellationToken ct)
    {
        if (!VmNameValidator.IsValid(name)) return (null, Problems.BadRequest($"VM name is invalid: {VmNameValidator.Rule}"));
        var vm = await vms.GetAsync(name, ct).ConfigureAwait(false);
        if (vm is null) return (null, Problems.NotFound($"No VM named '{name}'."));
        var own = Ownership.VmTokenCanAccessVm(http.User.VmTokenName(), vm.Name);
        if (vm.Kind != VmKind.Primary && (own || Ownership.SameName(vm.Parent, http.User.VmTokenName())))
            return (null, CodedProblems.Create(403, "vault-child", "Child VMs have no key vault access."));
        if (!own) return (null, Problems.Forbidden($"You may not access VM '{name}'."));
        if (ApiHelpers.FenceDeleting(vm) is { } fenced) return (null, fenced);
        return (vm, null);
    }

    private static async Task<(JsonObject? Body, IResult? Failure)> ReadObjectAsync(HttpRequest request, int max, CancellationToken ct)
    {
        if (request.ContentLength > max) return (null, CodedProblems.Create(413, "too-large", $"The body is larger than {max} bytes."));
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > max) return (null, CodedProblems.Create(413, "too-large", $"The body is larger than {max} bytes."));
                buffer.Write(chunk, 0, read);
            }
            try { return JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) is JsonObject body ? (body, null) : (null, CodedProblems.Validation("body", "Expected a JSON object.")); }
            catch (JsonException) { return (null, CodedProblems.Validation("body", "Expected a JSON object.")); }
        }
        finally
        {
            // An add carries the secret: do not leave its bytes in a pooled buffer.
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
            CryptographicOperations.ZeroMemory(chunk);
        }
    }

    private static Task<IResult> SubmitAsync(string name, HttpContext http, IVmRepository vms, VaultHostService vault, IHostConfigStore config, ConstructdOptions options, CancellationToken ct) => Run(http, async () =>
    {
        var (vm, failure) = await GuestVmAsync(name, http, vms, ct).ConfigureAwait(false);
        if (failure is not null) return failure;
        var (body, invalid) = await ReadObjectAsync(http.Request, MaxGuestBody, ct).ConfigureAwait(false);
        if (invalid is not null) return invalid;
        var result = await vault.SubmitAsync(vm!, body!, ct).ConfigureAwait(false);
        var names = string.Join(" ", (body!["names"] as JsonArray ?? []).Take(20).Select(n => VaultProtocol.IsValidName(VaultProtocol.Text(n)) ? VaultProtocol.Text(n) : "?"));
        http.SetAuditDetail($"op={VaultProtocol.Sanitize(body.Str("op"), 20)}, names={names}, " +
            (result.PendingId is { } pending ? $"pending={pending}" : $"status={result.Response!.Str("status")}"));
        if (result.PendingId is not { } id) return Results.Ok(result.Response);
        // approveUrl: the guest's "waiting for approval" note, which T3 Code turns into a banner, links here.
        var (webUrl, _) = await WebAddressAsync(config, options, ct).ConfigureAwait(false);
        return Results.Accepted($"/api/v1/vms/{vm!.Name}/vault/requests/{id}", new { id, approveUrl = webUrl + "/vault/#request=" + Uri.EscapeDataString(id) });
    });

    private static Task<IResult> PollAsync(string name, string id, HttpContext http, IVmRepository vms, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        var (vm, failure) = await GuestVmAsync(name, http, vms, ct).ConfigureAwait(false);
        if (failure is not null) return failure;
        var wait = 0;
        if (http.Request.Query["wait"].ToString() is { Length: > 0 } text && (!int.TryParse(text, out wait) || wait < 0))
            return CodedProblems.Validation("wait", $"Expected 0 to {MaxWaitSeconds} seconds.");
        var response = await vault.PollAsync(vm!, id, TimeSpan.FromSeconds(Math.Min(wait, MaxWaitSeconds)), ct).ConfigureAwait(false);
        return response is null ? Results.NoContent() : Results.Ok(response);
    });

    private static Task<IResult> GetScrubsAsync(string name, HttpContext http, IVmRepository vms, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        var (vm, failure) = await GuestVmAsync(name, http, vms, ct).ConfigureAwait(false);
        return failure ?? Results.Ok(await vault.ScrubsAsync(vm!, ct).ConfigureAwait(false));
    });

    private static Task<IResult> PostScrubAsync(string name, string id, HttpContext http, IVmRepository vms, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        var (vm, failure) = await GuestVmAsync(name, http, vms, ct).ConfigureAwait(false);
        if (failure is not null) return failure;
        var (body, invalid) = await ReadObjectAsync(http.Request, MaxScrubBody, ct).ConfigureAwait(false);
        if (invalid is not null) return invalid;
        http.SetAuditDetail($"job={id}, step={VaultProtocol.Sanitize(body!.Str("step"), 10)}");
        await vault.ScrubResultAsync(vm!, id, body!, ct).ConfigureAwait(false);
        return Results.NoContent();
    });
}
