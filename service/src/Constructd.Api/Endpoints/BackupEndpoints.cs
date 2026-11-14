using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Api.Auth;
using Constructd.Api.Backups;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Microsoft.Net.Http.Headers;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Host-stored config backups (docs/plans/host-config-backups.md §3.5). The guest uploads with its VM
/// token; the owner (or an admin) lists, downloads, deletes, requests a backup and sets the per-VM
/// schedule. Backups belong to <em>(owner, VM name)</em>, so a reinstalled VM of the same owner and
/// name sees its predecessor's. The host only ever holds ciphertext and wrapped keys.
/// <para>
/// The guardrail of docs/plans/construct-platform-design.md 10.2: an upload counts as host-requested only
/// when it consumes the host's open request; any other is the VM's slot. A VM token deletes, pins and
/// unpins nothing; deleting the protected backup takes the user's explicit <c>?confirm=true</c>.
/// </para>
/// </summary>
public static partial class BackupEndpoints
{
    private const string Root = "/vms/{name}/backups";
    private const string Item = Root + "/{id:regex(^[0-9a-f]{{32}}$)}";

    public static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost(Root, CreateAsync).RequireAuthorization(Policies.VmToken).Audited("backup.create").WithName("CreateBackup")
            .Produces<BackupCreatedResponse>(StatusCodes.Status201Created);
        api.MapPut(Item + "/keys", PutKeysAsync).RequireAuthorization(Policies.VmToken).Audited("backup.keys").WithName("PutBackupKeys")
            .DocumentBody("application/json", new OpenApiSchema { Type = JsonSchemaType.Object, Description = "The keys file (construct-backup-keys/1)." })
            .Produces(StatusCodes.Status204NoContent);
        api.MapPut(Item + "/content", UploadAsync).RequireAuthorization(Policies.VmToken).Audited("backup.upload").WithName("UploadBackup")
            .DocumentBody("application/octet-stream", new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" })
            .Produces<BackupUploadedResponse>();
        api.MapDelete(Item, DeleteAsync).RequireAuthorization(Policies.VmScoped).Audited("backup.delete").WithName("DeleteBackup").Produces(StatusCodes.Status204NoContent);
        api.MapPost(Root + "/report", ReportAsync).RequireAuthorization(Policies.VmToken).Audited("backup.report").WithName("ReportBackup")
            .Produces(StatusCodes.Status204NoContent);
        api.MapPost(Item + "/restored", RestoredAsync).RequireAuthorization(Policies.VmToken).Audited("backup.restored").WithName("BackupRestored")
            .Produces(StatusCodes.Status204NoContent);
        api.MapPost(Item + "/pin", PinAsync).RequireAuthorization(Policies.User).Audited("backup.pin").WithName("PinBackup")
            .Produces(StatusCodes.Status204NoContent);
        api.MapDelete(Item + "/pin", UnpinAsync).RequireAuthorization(Policies.User).Audited("backup.unpin").WithName("UnpinBackup")
            .Produces(StatusCodes.Status204NoContent);
        api.MapGet(Root, ListAsync).RequireAuthorization(Policies.VmScoped).WithName("ListBackups").Produces<BackupListResponse>();
        api.MapGet(Item + "/content", DownloadAsync).RequireAuthorization(Policies.VmScoped).WithName("DownloadBackup")
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream");
        api.MapGet(Item + "/keys", GetKeysAsync).RequireAuthorization(Policies.VmScoped).WithName("GetBackupKeys")
            .Produces<JsonElement>(StatusCodes.Status200OK, "application/json");
        api.MapGet(Root + "/status", StatusAsync).RequireAuthorization(Policies.VmScoped).WithName("GetBackupStatus").Produces<BackupStatusResponse>();
        api.MapPost(Root + "/request", RequestAsync).RequireAuthorization(Policies.User).Audited("backup.request").WithName("RequestBackup").Produces<BackupRequestedResponse>(StatusCodes.Status202Accepted);
        api.MapGet(Root + "/settings", GetSettingsAsync).RequireAuthorization(Policies.User).WithName("GetBackupSettings").Produces<BackupSettingsResponse>();
        api.MapPut(Root + "/settings", PutSettingsAsync).RequireAuthorization(Policies.User).Audited("backup.settings").WithName("PutBackupSettings").Produces<BackupSettingsResponse>();
        return api;
    }

    /// <summary>
    /// Documents a body the handler reads itself. Only the OpenAPI document changes: <c>Accepts</c> would also make
    /// routing refuse other content types, which the guest's routes never did.
    /// </summary>
    private static RouteHandlerBuilder DocumentBody(this RouteHandlerBuilder route, string contentType, OpenApiSchema schema) =>
        route.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType> { [contentType] = new() { Schema = schema } },
            };
            return Task.CompletedTask;
        });

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();

    private const int MaxDetail = 500;

    // ── shared plumbing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The VM behind <c>{name}</c>, authorized with <paramref name="policy"/>. Only primaries have
    /// backups (and VM tokens), so a child answers <c>not-a-primary</c>.
    /// </summary>
    private static async Task<(Vm? Vm, IResult? Failure)> VmAsync(string name, string policy, HttpContext http,
        IVmRepository vms, IAuthorizationService authorization, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, policy, ct).ConfigureAwait(false);
        if (!lookup.Ok) return (null, lookup.Failure);
        return lookup.Vm!.Kind == VmKind.Primary
            ? (lookup.Vm, null)
            : (null, Fail(http, 409, "not-a-primary", "Only primary VMs have config backups."));
    }

    internal static IResult Fail(HttpContext http, int status, string code, string detail, Dictionary<string, object?>? extra = null)
    {
        http.AppendAuditDetail("code=" + code);
        return CodedProblems.Create(status, code, detail, extra: extra);
    }

    private static IResult NotFound(HttpContext http) => Fail(http, 404, "not-found", "No such backup of this VM.");

    private static IResult NotOpen(HttpContext http) =>
        Fail(http, 409, "backup-not-open", "This backup is not an open upload session of this VM.");

    // ── the guest ───────────────────────────────────────────────────────────────

    /// <summary>What a backup counts as in the list: <c>slot</c>, or the host's request it answered.</summary>
    internal static string Kind(ConfigBackup backup) => backup.IsSlot ? BackupTriggers.Slot : backup.Trigger switch
    {
        BackupTriggers.Scheduled => BackupTriggers.Scheduled,
        BackupTriggers.PreReinstall => BackupTriggers.PreReinstall,
        _ => "requested",
    };

    /// <summary>
    /// Opens an upload session. While the host has a request open, the first create consumes it: the session
    /// is host-requested with the request's trigger, whatever the VM sent, and an open slot session is
    /// cancelled. Any other create is the VM's slot: one open session, at most one per 30 minutes, within
    /// <see cref="BackupCoordinator.SlotMaxBytes"/>, and it touches no schedule state.
    /// </summary>
    private static async Task<IResult> CreateAsync(string name, BackupCreateRequest? request, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IMaintenanceGate maintenance, IClock clock, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        if (ApiHelpers.FenceDeleting(vm) is { } fenced) return fenced;
        // No new session once the host drains for maintenance: its upload would be refused anyway.
        using var activity = maintenance.TryEnter("backup-create", http.TraceIdentifier, vm.Name);
        if (activity is null) return await MaintenanceFilter.RefusedAsync(http);
        if (!BackupTriggers.IsValid(request?.Trigger)) return CodedProblems.Validation("trigger", "Expected scheduled, manual or pre-reinstall.");
        if (request!.EstimatedBytes is not long estimate || estimate < 0)
            return CodedProblems.Validation("estimatedBytes", "Expected a non-negative number of bytes.");
        if (request.FileCount is < 0) return CodedProblems.Validation("fileCount", "Expected a non-negative number of files.");
        var host = await backups.ConfigAsync(ct);
        if (!host.Enabled) return Fail(http, 403, "backups-disabled", "Config backups are disabled on this host.");

        var store = backups.Store;
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var now = clock.UtcNow;
        foreach (var stale in (await store.ListAsync(vm.Owner, vm.Name, ct)).Where(b => backups.IsStale(b, now)).ToList())
            await backups.DeleteLockedAsync(stale, ct);
        var existing = await store.ListAsync(vm.Owner, vm.Name, ct);
        var state = await store.GetVmAsync(vm.Owner, vm.Name, ct) ?? new BackupVmState(vm.Owner, vm.Name);
        var requested = BackupSchedule.RequestPending(state, now);
        string trigger;
        if (requested)
        {
            if (existing.Any(b => !b.IsSlot && (b.IsOpen || backups.IsUploading(b.Id))))
                return Fail(http, 409, "backup-in-progress", "Another backup of this VM is in progress.");
            // The host's request wins over the VM's own upload.
            foreach (var slot in existing.Where(b => b.IsSlot && b.IsOpen).ToList()) await backups.DeleteLockedAsync(slot, ct);
            trigger = state.RequestTrigger ?? BackupTriggers.Manual;
            // Trying to open a session is the attempt, and it consumes the request either way.
            await store.SaveVmAsync(state with { LastAttemptAt = now, RequestPendingAt = null, RequestTrigger = null }, ct);
        }
        else
        {
            if (existing.Any(b => b.IsOpen || backups.IsUploading(b.Id)))
                return Fail(http, 409, "backup-in-progress", "Another backup of this VM is in progress.");
            if (state.SlotLastAt is { } last && now - last < BackupSchedule.SlotInterval)
                return Fail(http, 429, "slot-too-soon", "This VM's own backups are limited to one per 30 minutes.",
                    new() { ["retryAfterSeconds"] = (long)Math.Ceiling((last + BackupSchedule.SlotInterval - now).TotalSeconds) });
            trigger = BackupTriggers.Slot;
        }
        var slotUpload = !requested;

        var complete = existing.Where(b => b.IsComplete && b.IsSlot == slotUpload).OrderByDescending(b => b.CreatedAt).ToList();
        var stored = BackupCoordinator.StoredBytes(existing, slotUpload);
        var expected = Math.Max(estimate, complete.FirstOrDefault()?.SizeBytes ?? 0);
        var free = backups.Files.FreeBytes();
        var need = (long)Math.Min(long.MaxValue, Math.Ceiling(expected * 1.2m));
        if (free - (decimal)need < host.MinFreeBytes)
            return Fail(http, 507, "host-disk-space", "The host does not have enough free space for this backup.",
                new() { ["freeBytes"] = free, ["needBytes"] = need, ["minFreeBytes"] = host.MinFreeBytes });
        if (slotUpload && expected > backups.SlotMaxBytes)
            return Fail(http, 507, "backup-quota", "This backup would exceed the VM's own backup slot on the host.",
                new() { ["estimatedBytes"] = expected, ["slotMaxBytes"] = backups.SlotMaxBytes });
        if (!slotUpload && stored + (decimal)expected > BackupSchedule.Quota(host))
            return Fail(http, 507, "backup-quota", "This backup would exceed the VM's backup quota on the host.",
                new() { ["storedBytes"] = stored, ["estimatedBytes"] = expected, ["maxBytesPerVm"] = BackupSchedule.Quota(host) });
        if (slotUpload) await store.SaveVmAsync(state with { SlotLastAt = now }, ct);

        var id = Guid.NewGuid().ToString("n");
        var backup = new ConfigBackup(id, vm.Owner, vm.Name, trigger, BackupStates.Open, now,
            Origin: slotUpload ? BackupOrigins.Slot : BackupOrigins.Host, FileCount: request.FileCount);
        await store.InsertAsync(backup, ct);
        var maxBytes = backups.MaxBytes(host, slotUpload, free, stored);
        CodedProblems.Audit(http, "backup.create", vm.Owner, target: id,
            extra: $"trigger={trigger}, sent={request.Trigger}, estimatedBytes={estimate}, maxBytes={maxBytes}");
        return TypedResults.Created((string?)null, new BackupCreatedResponse(id, maxBytes, Kind(backup)));
    }

    private static async Task<IResult> PutKeysAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        var body = await ReadLimitedAsync(http.Request, BackupKeysRules.MaxBytes, ct);
        if (body is null) return CodedProblems.Validation("keys", $"The keys file is larger than {BackupKeysRules.MaxBytes} bytes.");
        if (BackupKeysRules.Validate(body, id) is { } invalid) return CodedProblems.Validation(invalid.Field, invalid.Reason);

        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var backup = await backups.Store.GetAsync(id, ct);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsOpen || backups.IsUploading(id)) return NotOpen(http);
        if (!await backups.Store.SetKeysAsync(id, Encoding.UTF8.GetString(body).Trim(), ct)) return NotOpen(http);
        CodedProblems.Audit(http, "backup.keys", vm.Owner, target: id, extra: $"bytes={body.Length}");
        return Results.NoContent();
    }

    /// <summary>
    /// Streams the payload into <c>&lt;id&gt;.age.partial</c> while hashing and counting it, then renames it
    /// and completes the session (<see cref="BackupUpload"/>). Any failure removes the partial file and
    /// closes the session as failed.
    /// </summary>
    private static async Task<IResult> UploadAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IMaintenanceGate maintenance, IClock clock)
    {
        var ct = http.RequestAborted;
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        // A long upload holds the host out of maintenance; while maintenance runs, none starts.
        using var activity = maintenance.TryEnter("backup-upload", id + ":" + http.TraceIdentifier, vm.Name);
        if (activity is null) return await MaintenanceFilter.RefusedAsync(http);

        ConfigBackup backup;
        CancellationTokenSource upload;
        await using (await backups.LockAsync(vm.Owner, vm.Name, ct))
        {
            var found = await backups.Store.GetAsync(id, ct);
            if (found is null || !BackupCoordinator.Belongs(found, vm) || !found.IsOpen) return NotOpen(http);
            if (backups.BeginUpload(id, ct) is not { } registered) return NotOpen(http);
            backup = found;
            upload = registered;
        }
        try
        {
            return await new BackupUpload(http, vm, backup, upload, backups, clock).RunAsync();
        }
        finally
        {
            // Normally ended under the pair's lock already; this covers an unexpected exception.
            backups.EndUpload(id, upload);
        }
    }

    private static async Task<IResult> ReportAsync(string name, BackupReportRequest? request, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IClock clock, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        if (!BackupTriggers.IsValid(request?.Trigger)) return CodedProblems.Validation("trigger", "Expected scheduled, manual or pre-reinstall.");
        if (request!.Result is not ("skipped" or "failed" or "enrolled")) return CodedProblems.Validation("result", "Expected skipped, failed or enrolled.");
        if (request.Reason is not { } reason || !ReasonPattern().IsMatch(reason))
            return CodedProblems.Validation("reason", "Expected a reason code (lowercase letters, digits and hyphens).");
        if (request.Detail is { Length: > MaxDetail }) return CodedProblems.Validation("detail", $"At most {MaxDetail} characters.");

        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        if (request.Result == "enrolled")
        {
            // A PC key was enrolled on the VM: a run that failed for lack of one is over.
            var cleared = await backups.RecordEnrolmentLockedAsync(vm.Owner, vm.Name, ct);
            CodedProblems.Audit(http, "backup.report", vm.Owner, extra: $"result=enrolled, cleared={(cleared ? "no-recipients" : "nothing")}");
            return Results.NoContent();
        }
        // A report counts for the schedule only while it answers the host: an open request (it is the attempt,
        // and consumes the request), or the host-requested attempt just made whose failure is not yet known
        // (recorded without moving the attempt). Anything else is about the VM's slot and is not schedule state.
        var now = clock.UtcNow;
        var state = await backups.Store.GetVmAsync(vm.Owner, vm.Name, ct);
        string recorded;
        if (BackupSchedule.RequestPending(state, now))
        {
            await backups.RecordFailureLockedAsync(vm.Owner, vm.Name, request.Trigger!, reason, request.Detail, answersRequest: true, ct);
            recorded = "request";
        }
        else if (state is { LastAttemptAt: { } attempt } && now - attempt < BackupSchedule.StaleSession &&
                 !(state.LastSuccessAt >= attempt) && !(state.LastFailure?.At >= attempt))
        {
            await backups.Store.SaveVmAsync(state with
            {
                LastFailure = new BackupFailure(now, request.Trigger!, reason, request.Detail),
                FailingSince = state.FailingSince ?? now,
            }, ct);
            recorded = "attempt";
        }
        else recorded = "ignored";
        CodedProblems.Audit(http, "backup.report", vm.Owner, extra: $"trigger={request.Trigger}, result={request.Result}, reason={reason}, recorded={recorded}");
        return Results.NoContent();
    }

    private static async Task<IResult> RestoredAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var backup = await backups.Store.GetAsync(id, ct);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsComplete) return NotFound(http);
        // Recorded only: a VM token never unpins (docs/plans/construct-platform-design.md 10.1). The pin of a
        // pre-reinstall backup runs out by itself, or the user releases it.
        CodedProblems.Audit(http, "backup.restored", vm.Owner, target: id);
        return Results.NoContent();
    }

    // ── the owner: pins ─────────────────────────────────────────────────────────

    /// <summary>The user pins a complete host-requested backup for the host's <c>pinDays</c> from now.</summary>
    private static async Task<IResult> PinAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IClock clock, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        var host = await backups.ConfigAsync(ct);
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var backup = await backups.Store.GetAsync(id, ct);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsComplete) return NotFound(http);
        if (backup.IsSlot) return Fail(http, 409, "backup-is-slot", "The VM's own slot backup is replaced by its next upload and cannot be pinned.");
        var until = clock.UtcNow.AddDays(host.PinDays);
        await backups.Store.SetPinAsync(id, until, ct);
        CodedProblems.Audit(http, "backup.pin", vm.Owner, target: id, extra: $"until={until:O}");
        return Results.NoContent();
    }

    private static async Task<IResult> UnpinAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var backup = await backups.Store.GetAsync(id, ct);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsComplete) return NotFound(http);
        await backups.Store.SetPinAsync(id, null, ct);
        CodedProblems.Audit(http, "backup.unpin", vm.Owner, target: id);
        return Results.NoContent();
    }

    // ── the guest or the owner ──────────────────────────────────────────────────

    /// <param name="confirm">The user's explicit confirmation, needed to delete the protected backup.</param>
    private static async Task<IResult> DeleteAsync(string name, string id, bool? confirm, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IClock clock, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var backup = await backups.Store.GetAsync(id, ct);
        // The guest may only abandon its own open session; the owner may delete any backup of the VM.
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || http.User.IsVmToken() && !backup.IsOpen) return NotFound(http);
        if (confirm != true && backup.IsComplete &&
            BackupRotation.Protected(await backups.Store.ListAsync(vm.Owner, vm.Name, ct), clock.UtcNow, backups.MinHorizonDays)?.Id == id)
            return Fail(http, 409, "backup-protected", "This is the backup the retention rule keeps; delete it with ?confirm=true.");
        await backups.DeleteLockedAsync(backup, ct);
        CodedProblems.Audit(http, "backup.delete", vm.Owner, target: id, extra: $"state={backup.State}, sizeBytes={backup.SizeBytes ?? 0}");
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(string name, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IClock clock, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        var now = clock.UtcNow;
        var all = await backups.Store.ListAsync(vm.Owner, vm.Name, ct);
        var protectedId = BackupRotation.Protected(all, now, backups.MinHorizonDays)?.Id;
        var list = all.Where(b => b.IsComplete).OrderByDescending(b => b.CreatedAt);
        return Results.Ok(new BackupListResponse([.. list.Select(b => new BackupSummaryResponse(
            b.Id, b.CreatedAt, b.Trigger, b.SizeBytes, b.Sha256, b.IsPinned(now) ? b.PinnedUntil : null,
            Kind(b), b.IsPinned(now), b.Id == protectedId, b.FileCount))]));
    }

    private static async Task<IResult> DownloadAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        var backup = await backups.Store.GetAsync(id, ct);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsComplete) return NotFound(http);
        var stream = backups.Files.OpenRead(vm.Owner, vm.Name, id);
        if (stream is null) return NotFound(http);
        return Results.Stream(stream, "application/octet-stream", entityTag: new EntityTagHeaderValue("\"" + backup.Sha256 + "\""));
    }

    private static async Task<IResult> GetKeysAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        var backup = await backups.Store.GetAsync(id, ct);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsComplete || backup.KeysJson is null) return NotFound(http);
        return Results.Text(backup.KeysJson, "application/json", Encoding.UTF8);
    }

    private static async Task<IResult> StatusAsync(string name, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IClock clock, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        var now = clock.UtcNow;
        var host = await backups.ConfigAsync(ct);
        var state = await backups.Store.GetVmAsync(vm.Owner, vm.Name, ct);
        var complete = (await backups.Store.ListAsync(vm.Owner, vm.Name, ct)).Where(b => b.IsComplete).ToList();
        return Results.Ok(new BackupStatusResponse(
            Enabled: host.Enabled,
            LastAttemptAt: state?.LastAttemptAt,
            LastSuccessAt: state?.LastSuccessAt,
            LastFailure: state?.LastFailure,
            FailingSince: state?.FailingSince,
            RequestPending: host.Enabled && BackupSchedule.RequestPending(state, now),
            Count: complete.Count,
            TotalBytes: complete.Sum(b => b.SizeBytes ?? 0),
            NextDueAt: BackupSchedule.NextDueAt(host, state, vm.Created, now)));
    }

    // ── the owner ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The user asks for a backup: the host opens its request (replacing an open one), the heartbeat asks
    /// the VM, and the VM's next create consumes it. A <c>pre-reinstall</c> request pins the backup it gets.
    /// </summary>
    private static async Task<IResult> RequestAsync(string name, BackupRequestRequest? request, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        if (ApiHelpers.FenceDeleting(vm) is { } fenced) return fenced;
        var trigger = request?.Trigger ?? BackupTriggers.Manual;
        if (trigger is not (BackupTriggers.Manual or BackupTriggers.PreReinstall))
            return CodedProblems.Validation("trigger", "Expected manual or pre-reinstall.");
        var host = await backups.ConfigAsync(ct);
        if (!host.Enabled) return Fail(http, 403, "backups-disabled", "Config backups are disabled on this host.");
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        await backups.OpenRequestLockedAsync(vm.Owner, vm.Name, trigger, ct);
        CodedProblems.Audit(http, "backup.request", vm.Owner, target: vm.Name, extra: $"trigger={trigger}");
        return Results.Accepted(null, new BackupRequestedResponse(true, trigger));
    }

    private static async Task<IResult> GetSettingsAsync(string name, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        var host = await backups.ConfigAsync(ct);
        return Results.Ok(Settings(host, await backups.Store.GetVmAsync(vm.Owner, vm.Name, ct)));
    }

    private static async Task<IResult> PutSettingsAsync(string name, JsonElement request, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        if (request.ValueKind != JsonValueKind.Object) return CodedProblems.Validation("settings", "Expected an object.");
        foreach (var property in request.EnumerateObject())
            if (property.Name is not ("intervalHours" or "keep")) return CodedProblems.Validation(property.Name, "Unknown field.");
        var host = await backups.ConfigAsync(ct);
        if (!TryReadOverride(request, "intervalHours", 0, 720, out var interval))
            return CodedProblems.Validation("intervalHours", "Expected null (the host default) or 0 to 720 hours.");
        if (!TryReadOverride(request, "keep", 1, host.MaxKeep, out var keep))
            return CodedProblems.Validation("keep", $"Expected null (the host default) or 1 to {host.MaxKeep}.");

        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var state = await backups.Store.GetVmAsync(vm.Owner, vm.Name, ct) ?? new BackupVmState(vm.Owner, vm.Name);
        state = state with { IntervalHours = interval, Keep = keep };
        await backups.Store.SaveVmAsync(state, ct);
        CodedProblems.Audit(http, "backup.settings", vm.Owner, target: vm.Name,
            extra: $"intervalHours={interval?.ToString() ?? "default"}, keep={keep?.ToString() ?? "default"}");
        return Results.Ok(Settings(host, state));
    }

    private static bool TryReadOverride(JsonElement request, string field, int min, int max, out int? value)
    {
        value = null;
        if (!request.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number) || number < min || number > max) return false;
        value = number;
        return true;
    }

    private static BackupSettingsResponse Settings(BackupsConfig host, BackupVmState? state) => new(
        state?.IntervalHours,
        state?.Keep,
        new BackupEffectiveSettings(host.Enabled, BackupSchedule.EffectiveIntervalHours(host, state), BackupSchedule.EffectiveKeep(host, state)),
        new BackupHostSettings(host.Enabled, host.IntervalHours, host.Keep, host.MaxKeep));

    /// <summary>Reads at most <paramref name="max"/> bytes of the body; null when it is longer.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpRequest request, int max, CancellationToken ct)
    {
        if (request.ContentLength > max) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
