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
using Microsoft.Net.Http.Headers;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Host-stored config backups (docs/plans/host-config-backups.md §3.5). The guest uploads with its VM
/// token; the owner (or an admin) lists, downloads, deletes, requests a backup and sets the per-VM
/// schedule. Backups belong to <em>(owner, VM name)</em>, so a reinstalled VM of the same owner and
/// name sees its predecessor's. The host only ever holds ciphertext and wrapped keys.
/// </summary>
public static partial class BackupEndpoints
{
    private const string Root = "/vms/{name}/backups";
    private const string Item = Root + "/{id:regex(^[0-9a-f]{{32}}$)}";

    public static RouteGroupBuilder MapBackupEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost(Root, CreateAsync).RequireAuthorization(Policies.VmToken).Audited("backup.create").WithName("CreateBackup");
        api.MapPut(Item + "/keys", PutKeysAsync).RequireAuthorization(Policies.VmToken).Audited("backup.keys").WithName("PutBackupKeys");
        api.MapPut(Item + "/content", UploadAsync).RequireAuthorization(Policies.VmToken).Audited("backup.upload").WithName("UploadBackup");
        api.MapDelete(Item, DeleteAsync).RequireAuthorization(Policies.VmScoped).Audited("backup.delete").WithName("DeleteBackup");
        api.MapPost(Root + "/report", ReportAsync).RequireAuthorization(Policies.VmToken).Audited("backup.report").WithName("ReportBackup");
        api.MapPost(Item + "/restored", RestoredAsync).RequireAuthorization(Policies.VmToken).Audited("backup.restored").WithName("BackupRestored");
        api.MapGet(Root, ListAsync).RequireAuthorization(Policies.VmScoped).WithName("ListBackups");
        api.MapGet(Item + "/content", DownloadAsync).RequireAuthorization(Policies.VmScoped).WithName("DownloadBackup");
        api.MapGet(Item + "/keys", GetKeysAsync).RequireAuthorization(Policies.VmScoped).WithName("GetBackupKeys");
        api.MapGet(Root + "/status", StatusAsync).RequireAuthorization(Policies.VmScoped).WithName("GetBackupStatus");
        api.MapPost(Root + "/request", RequestAsync).RequireAuthorization(Policies.User).Audited("backup.request").WithName("RequestBackup");
        api.MapGet(Root + "/settings", GetSettingsAsync).RequireAuthorization(Policies.User).WithName("GetBackupSettings");
        api.MapPut(Root + "/settings", PutSettingsAsync).RequireAuthorization(Policies.User).Audited("backup.settings").WithName("PutBackupSettings");
        return api;
    }

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
        var host = await backups.ConfigAsync(ct);
        if (!host.Enabled) return Fail(http, 403, "backups-disabled", "Config backups are disabled on this host.");

        var store = backups.Store;
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var now = clock.UtcNow;
        var existing = await store.ListAsync(vm.Owner, vm.Name, ct);
        foreach (var stale in existing.Where(b => backups.IsStale(b, now)).ToList()) await backups.DeleteLockedAsync(stale, ct);
        if (existing.Any(b => b.IsOpen && now - b.CreatedAt < BackupSchedule.StaleSession || backups.IsUploading(b.Id)))
            return Fail(http, 409, "backup-in-progress", "Another backup of this VM is in progress.");

        // Trying to open a session is the attempt, and it answers a pending request either way.
        var state = await store.GetVmAsync(vm.Owner, vm.Name, ct) ?? new BackupVmState(vm.Owner, vm.Name);
        await store.SaveVmAsync(state with { LastAttemptAt = now, RequestPendingAt = null }, ct);

        var complete = existing.Where(b => b.IsComplete).OrderByDescending(b => b.CreatedAt).ToList();
        var stored = complete.Sum(b => b.SizeBytes ?? 0);
        var expected = Math.Max(estimate, complete.FirstOrDefault()?.SizeBytes ?? 0);
        var free = backups.Files.FreeBytes();
        var need = (long)Math.Min(long.MaxValue, Math.Ceiling(expected * 1.2m));
        if (free - (decimal)need < host.MinFreeBytes)
            return Fail(http, 507, "host-disk-space", "The host does not have enough free space for this backup.",
                new() { ["freeBytes"] = free, ["needBytes"] = need, ["minFreeBytes"] = host.MinFreeBytes });
        if (host.MaxBytesPerVm is long quota && stored + (decimal)expected > quota)
            return Fail(http, 507, "backup-quota", "This backup would exceed the VM's backup quota on the host.",
                new() { ["storedBytes"] = stored, ["estimatedBytes"] = expected, ["maxBytesPerVm"] = quota });

        var id = Guid.NewGuid().ToString("n");
        await store.InsertAsync(new ConfigBackup(id, vm.Owner, vm.Name, request.Trigger!, BackupStates.Open, now), ct);
        var maxBytes = BackupCoordinator.MaxBytes(host, free, stored);
        CodedProblems.Audit(http, "backup.create", vm.Owner, target: id,
            extra: $"trigger={request.Trigger}, estimatedBytes={estimate}, maxBytes={maxBytes}");
        return TypedResults.Created((string?)null, new { id, maxBytes });
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
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        if (!BackupTriggers.IsValid(request?.Trigger)) return CodedProblems.Validation("trigger", "Expected scheduled, manual or pre-reinstall.");
        if (request!.Result is not ("skipped" or "failed")) return CodedProblems.Validation("result", "Expected skipped or failed.");
        if (request.Reason is not { } reason || !ReasonPattern().IsMatch(reason))
            return CodedProblems.Validation("reason", "Expected a reason code (lowercase letters, digits and hyphens).");
        if (request.Detail is { Length: > MaxDetail }) return CodedProblems.Validation("detail", $"At most {MaxDetail} characters.");

        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        await backups.RecordFailureLockedAsync(vm.Owner, vm.Name, request.Trigger!, reason, request.Detail, ct);
        CodedProblems.Audit(http, "backup.report", vm.Owner, extra: $"trigger={request.Trigger}, result={request.Result}, reason={reason}");
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
        await backups.Store.UnpinAsync(id, ct);
        CodedProblems.Audit(http, "backup.restored", vm.Owner, target: id);
        return Results.NoContent();
    }

    // ── the guest or the owner ──────────────────────────────────────────────────

    private static async Task<IResult> DeleteAsync(string name, string id, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmSelfOrOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var backup = await backups.Store.GetAsync(id, ct);
        // The guest may only abandon its own open session; the owner may delete any backup of the VM.
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || http.User.IsVmToken() && !backup.IsOpen) return NotFound(http);
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
        var list = (await backups.Store.ListAsync(vm.Owner, vm.Name, ct)).Where(b => b.IsComplete).OrderByDescending(b => b.CreatedAt);
        return Results.Ok(new
        {
            backups = list.Select(b => new
            {
                id = b.Id, createdAt = b.CreatedAt, trigger = b.Trigger, sizeBytes = b.SizeBytes, sha256 = b.Sha256,
                pinnedUntil = b.IsPinned(now) ? b.PinnedUntil : null,
            }).ToList(),
        });
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
        return Results.Ok(new
        {
            enabled = host.Enabled,
            lastAttemptAt = state?.LastAttemptAt,
            lastSuccessAt = state?.LastSuccessAt,
            lastFailure = state?.LastFailure is { } last
                ? (object)new { at = last.At, trigger = last.Trigger, reason = last.Reason, detail = last.Detail }
                : null,
            failingSince = state?.FailingSince,
            requestPending = host.Enabled && BackupSchedule.RequestPending(state, now),
            count = complete.Count,
            totalBytes = complete.Sum(b => b.SizeBytes ?? 0),
            nextDueAt = BackupSchedule.NextDueAt(host, state, vm.Created, now),
        });
    }

    // ── the owner ───────────────────────────────────────────────────────────────

    private static async Task<IResult> RequestAsync(string name, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, BackupCoordinator backups, IClock clock, CancellationToken ct)
    {
        var (vm, failure) = await VmAsync(name, Policies.VmOwnerOrAdmin, http, vms, authorization, ct);
        if (vm is null) return failure!;
        if (ApiHelpers.FenceDeleting(vm) is { } fenced) return fenced;
        var host = await backups.ConfigAsync(ct);
        if (!host.Enabled) return Fail(http, 403, "backups-disabled", "Config backups are disabled on this host.");
        await using var held = await backups.LockAsync(vm.Owner, vm.Name, ct);
        var state = await backups.Store.GetVmAsync(vm.Owner, vm.Name, ct) ?? new BackupVmState(vm.Owner, vm.Name);
        await backups.Store.SaveVmAsync(state with { RequestPendingAt = clock.UtcNow }, ct);
        CodedProblems.Audit(http, "backup.request", vm.Owner, target: vm.Name);
        return Results.Accepted(null, new { requestPending = true });
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

    private static object Settings(BackupsConfig host, BackupVmState? state) => new
    {
        intervalHours = state?.IntervalHours,
        keep = state?.Keep,
        effective = new
        {
            enabled = host.Enabled,
            intervalHours = BackupSchedule.EffectiveIntervalHours(host, state),
            keep = BackupSchedule.EffectiveKeep(host, state),
        },
        host = new { enabled = host.Enabled, intervalHours = host.IntervalHours, keep = host.Keep, maxKeep = host.MaxKeep },
    };

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
