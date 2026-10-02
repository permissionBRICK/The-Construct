using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Auth;
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
        api.MapGet("/vault/entries", GetEntriesAsync).RequireAuthorization(Policies.User).WithName("GetVaultEntries");
        api.MapPut("/vault/entries", PutEntriesAsync).RequireAuthorization(Policies.User).Audited("vault.sync").WithName("PutVaultEntries");
        api.MapPut("/vault/settings", PutSettingsAsync).RequireAuthorization(Policies.User).Audited("vault.settings").WithName("PutVaultSettings");
        api.MapPost("/vms/{name}/vault/unlock", UnlockAsync).RequireAuthorization(Policies.User).Audited("vault.unlock").WithName("UnlockVault");
        api.MapGet("/vault/leases", GetLeasesAsync).RequireAuthorization(Policies.User).WithName("GetVaultLeases");
        api.MapDelete("/vault/leases/{id}", RevokeLeaseAsync).RequireAuthorization(Policies.User).Audited("vault.lease.revoke", "id").WithName("RevokeVaultLease");
        api.MapGet("/vault/devices", GetDevicesAsync).RequireAuthorization(Policies.User).WithName("GetVaultDevices");
        api.MapPost("/vault/devices", PairDeviceAsync).RequireAuthorization(Policies.User).Audited("vault.device.pair").WithName("PairVaultDevice");
        api.MapDelete("/vault/devices/{id}", RevokeDeviceAsync).RequireAuthorization(Policies.User).Audited("vault.device.revoke", "id").WithName("RevokeVaultDevice");
        api.MapGet("/vault/activity", GetActivityAsync).RequireAuthorization(Policies.User).WithName("GetVaultActivity");

        // ---- the user or a paired device -----------------------------------------------------
        api.MapGet("/vault/approvals", GetApprovals).RequireAuthorization(Policies.VaultApprover).WithName("GetVaultApprovals");
        api.MapPost("/vault/approvals/{id}", DecideAsync).RequireAuthorization(Policies.VaultApprover).Audited("vault.approval", "id").WithName("DecideVaultApproval");
        api.MapGet("/vault/files", GetFilesAsync).RequireAuthorization(Policies.VaultApprover).WithName("GetVaultFiles");
        api.MapPost("/vault/files/{id}", DecideFileAsync).RequireAuthorization(Policies.VaultApprover).Audited("vault.file", "id").WithName("DecideVaultFile");
        api.MapGet("/vault/device", WhoAmI).RequireAuthorization(Policies.VaultDevice).WithName("GetVaultDevice");

        // ---- the guest (its own primary VM's token) -----------------------------------------
        api.MapPost("/vms/{name}/vault/requests", SubmitAsync).RequireAuthorization(Policies.VmToken).Audited("vault.request").WithName("SubmitVaultRequest");
        api.MapGet("/vms/{name}/vault/requests/{id}", PollAsync).RequireAuthorization(Policies.VmToken).WithName("PollVaultRequest");
        api.MapGet("/vms/{name}/vault/scrubs", GetScrubsAsync).RequireAuthorization(Policies.VmToken).WithName("GetVaultScrubs");
        api.MapPost("/vms/{name}/vault/scrubs/{id}", PostScrubAsync).RequireAuthorization(Policies.VmToken).Audited("vault.scrub").WithName("PostVaultScrub");
        return api;
    }

    public sealed record EntryDto(string? Name, string? Description, bool? HasUsername, string? Payload, long? UpdatedAt, string? UpdatedBy, bool? Deleted);
    public sealed record EntriesRequest(List<EntryDto?>? Entries);
    public sealed record SettingsRequest(string? Mode, string? Key, string? KeyCheck);
    public sealed record UnlockRequest(string? Key);
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
        return Results.Ok(new
        {
            revision = view.Revision, mode = view.Mode, keyCheck = view.KeyCheck, unlockedVms = view.UnlockedVms,
            entries = view.Entries.Select(e => new { name = e.Name, description = e.Description, hasUsername = e.HasUsername, payload = e.Payload,
                updatedAt = e.UpdatedAt, updatedBy = e.UpdatedBy, deleted = e.Deleted }),
            devices = view.Devices.Select(Device),
        });
    }

    private static Task<IResult> PutEntriesAsync(EntriesRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        http.SetAuditTarget(Owner(http));
        if (request?.Entries is not { } list) return CodedProblems.Validation("entries", "Expected {entries:[…]}.");
        var entries = list.Select((e, i) => e is null
            ? throw new VaultProblemException(400, "validation", "Expected an entry object.", $"entries[{i}]")
            : new VaultEntry(e.Name ?? "", e.Description ?? "", e.HasUsername ?? false, e.Payload, e.UpdatedAt ?? 0, e.UpdatedBy ?? "", e.Deleted ?? false)).ToArray();
        var (revision, applied) = await vault.PutEntriesAsync(Owner(http), entries, ct).ConfigureAwait(false);
        http.SetAuditDetail($"entries={entries.Length}, applied={applied}, revision={revision}");
        return Results.Ok(new { revision, applied });
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
        await vault.UnlockAsync(vm, request?.Key, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static async Task<IResult> GetLeasesAsync(HttpContext http, VaultHostService vault, CancellationToken ct) =>
        Results.Ok(new
        {
            leases = (await vault.LeasesAsync(Owner(http), ct).ConfigureAwait(false)).Select(l => new
            {
                id = l.Id, vm = l.Vm, name = l.Name, usesLeft = l.UsesLeft, expiresAt = l.ExpiresAt.ToUnixTimeMilliseconds(), reason = l.Reason, origin = l.Origin,
            }),
        });

    private static Task<IResult> RevokeLeaseAsync(string id, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        await vault.RevokeLeaseAsync(Owner(http), id, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static object Device(VaultDevice d) => new
    {
        id = d.Id, label = d.Label, createdAt = d.CreatedAt.ToUnixTimeMilliseconds(), lastUsedAt = d.LastUsedAt?.ToUnixTimeMilliseconds(),
    };

    private static async Task<IResult> GetDevicesAsync(HttpContext http, VaultHostService vault, ConstructdOptions options, CancellationToken ct) =>
        Results.Ok(new { webUrl = options.VaultWebBase(), devices = (await vault.DevicesAsync(Owner(http), ct).ConfigureAwait(false)).Select(Device) });

    private static Task<IResult> PairDeviceAsync(DeviceRequest? request, HttpContext http, VaultHostService vault, ConstructdOptions options, CancellationToken ct) => Run(http, async () =>
    {
        http.SetAuditTarget(Owner(http));
        var (device, token) = await vault.PairDeviceAsync(Owner(http), request?.Label, ct).ConfigureAwait(false);
        http.SetAuditDetail($"device={device.Id}");
        return Results.Ok(new { id = device.Id, token, webUrl = options.VaultWebBase() });
    });

    private static Task<IResult> RevokeDeviceAsync(string id, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        await vault.RevokeDeviceAsync(Owner(http), id, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static async Task<IResult> GetActivityAsync(HttpContext http, VaultHostService vault, CancellationToken ct) =>
        Results.Ok(new
        {
            events = (await vault.ActivityAsync(Owner(http), ct).ConfigureAwait(false))
                .Select(e => new { at = e.At.ToUnixTimeMilliseconds(), vm = e.Vm, text = e.Text, warning = e.Warning }),
        });

    // ── the user or a paired device ─────────────────────────────────────────────

    private static IResult GetApprovals(HttpContext http, VaultHostService vault) =>
        Results.Ok(new { approvals = vault.Approvals(Owner(http)) });

    private static Task<IResult> DecideAsync(string id, DecisionRequest? request, HttpContext http, VaultHostService vault, CancellationToken ct) => Run(http, async () =>
    {
        if (request?.Decision is not ("approve" or "deny")) return CodedProblems.Validation("decision", "Expected approve or deny.");
        http.SetAuditDetail($"decision={request.Decision}, by={(http.User.IsVaultDevice() ? "device" : "user")}");
        await vault.DecideAsync(Owner(http), id, request.Decision == "approve", ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static async Task<IResult> GetFilesAsync(HttpContext http, VaultHostService vault, CancellationToken ct) =>
        Results.Ok(new { files = await vault.FilesAsync(Owner(http), ct).ConfigureAwait(false) });

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

    private static Task<IResult> SubmitAsync(string name, HttpContext http, IVmRepository vms, VaultHostService vault, ConstructdOptions options, CancellationToken ct) => Run(http, async () =>
    {
        var (vm, failure) = await GuestVmAsync(name, http, vms, ct).ConfigureAwait(false);
        if (failure is not null) return failure;
        var (body, invalid) = await ReadObjectAsync(http.Request, MaxGuestBody, ct).ConfigureAwait(false);
        if (invalid is not null) return invalid;
        var result = await vault.SubmitAsync(vm!, body!, ct).ConfigureAwait(false);
        var names = string.Join(" ", (body!["names"] as JsonArray ?? []).Take(20).Select(n => VaultProtocol.IsValidName(VaultProtocol.Text(n)) ? VaultProtocol.Text(n) : "?"));
        http.SetAuditDetail($"op={VaultProtocol.Sanitize(body.Str("op"), 20)}, names={names}, " +
            (result.PendingId is { } pending ? $"pending={pending}" : $"status={result.Response!.Str("status")}"));
        return result.PendingId is { } id
            // approveUrl: the guest's "waiting for approval" note, which T3 Code turns into a banner, links here.
            ? Results.Accepted($"/api/v1/vms/{vm!.Name}/vault/requests/{id}", new { id, approveUrl = options.VaultWebBase() + "/vault/#request=" + Uri.EscapeDataString(id) })
            : Results.Ok(result.Response);
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
