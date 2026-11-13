using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Provisioning from the engine (design 7.3, 7.5): <c>POST /vms/{name}/jobs</c> starts a
/// <c>provision</c>, <c>reinstall</c> or <c>restore</c> job, <c>GET /vms/{name}/provision-status</c> answers what waits for the next one.
/// Owner or admin, user credentials, primaries only.
/// </summary>
public static partial class ProvisionEndpoints
{
    public static RouteGroupBuilder MapProvisionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/vms/{name}/jobs", SubmitAsync).RequireAuthorization(Policies.User).Audited("vm.provision")
            .Accepts<VmJobRequest>("application/json").Produces<VmJobAcceptedResponse>(StatusCodes.Status202Accepted).WithName("StartVmJob");
        api.MapGet("/vms/{name}/provision-status", StatusAsync).RequireAuthorization(Policies.User)
            .Produces<ProvisionStatusResponse>().WithName("GetVmProvisionStatus");
        api.MapPost("/jobs/{id}/retry", RetryAsync).RequireAuthorization(Policies.User).Audited("job.retry")
            .Accepts<JobRetryRequest>("application/json").Produces<VmJobAcceptedResponse>(StatusCodes.Status202Accepted).WithName("RetryJob");
        api.MapGet("/vms/{name}/desired", GetDesiredAsync).RequireAuthorization(Policies.User)
            .Produces<VmDesiredStateResponse>().WithName("GetVmDesiredState");
        api.MapPut("/vms/{name}/desired", SetDesiredAsync).RequireAuthorization(Policies.User).Audited("vm.desired")
            .Accepts<VmDesiredState>("application/json").Produces<VmDesiredStateResponse>().WithName("SetVmDesiredState");
        return api;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex ProjectPattern();

    [GeneratedRegex(@"^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"^[A-Za-z0-9+/]*={0,2}$")]
    private static partial Regex Base64Pattern();

    private static async Task<IResult> SubmitAsync(string name, VmJobRequest request, HttpContext http, IVmRepository vms,
        ProvisionJobs provisioning, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        if (request.Kind is not (JobKinds.Provision or JobKinds.Reinstall or JobKinds.Restore))
            return CodedProblems.Validation("kind", "Expected 'provision', 'reinstall' or 'restore'.");
        if (request.Kind == JobKinds.Restore && !BackupScript.IsBackupId(request.BackupId)) return CodedProblems.Validation("backupId", "Expected 32 lowercase hex digits.");
        if (request.Kind == JobKinds.Restore && !BackupJobKey.IsIdentity(request.Secrets?.BackupKey))
            return CodedProblems.Validation("secrets.backupKey", "A restore needs the backup's key (AGE-SECRET-KEY-1...).");
        if (request.RequestId is null || !RequestIdPattern().IsMatch(request.RequestId)) return CodedProblems.Validation("requestId", "Expected 1-128 letters, digits, '.', '_', ':' or '-'.");
        if (request.Commit is not null && !SourceZipRules.ValidCommit(request.Commit)) return CodedProblems.Validation("commit", "Expected 40 lowercase hex digits.");
        if (Validate(request.Desired) is { } field) return CodedProblems.Validation(field, "Invalid value.");
        if (SecretBag(request.Secrets, request.Kind == JobKinds.Restore, out var bag) is { } invalid) return invalid;
        return await StartAsync(vm, http, provisioning, new ProvisionSubmission(request.RequestId, request.Commit, request.Desired,
            request.DeferProjectCommands == true, bag, request.Kind, request.Kind == JobKinds.Restore ? request.BackupId : null, request.FreshImage == true), ct);
    }

    private static IResult? SecretBag(ProvisionSecretsRequest? secrets, bool restore, out JobSecretBag bag)
    {
        bag = new JobSecretBag();
        secrets ??= new ProvisionSecretsRequest();
        foreach (var (field, value) in new[] { ("secrets.vmTokenB64", secrets.VmTokenB64), ("secrets.gitCloneCredentialsB64", secrets.GitCloneCredentialsB64), ("secrets.serveWebTokenB64", secrets.ServeWebTokenB64) })
            if (value is not null && !Base64Pattern().IsMatch(value)) return CodedProblems.Validation(field, "Expected base64.");
        if (secrets.SudoPassword is { } pw && pw.Any(c => c is '\n' or '\r' or '\0')) return CodedProblems.Validation("secrets.sudoPassword", "Expected one line.");
        bag.Set(ProvisionScript.VmTokenVariable, secrets.VmTokenB64);
        bag.Set(ProvisionScript.CloneCredentialsVariable, secrets.GitCloneCredentialsB64);
        bag.Set(ProvisionScript.ServeWebTokenVariable, secrets.ServeWebTokenB64);
        bag.Set(ProvisionJob.SudoPasswordSecret, secrets.SudoPassword, base64: false);
        if (restore) bag.Set(ProvisionJob.BackupKeySecret, secrets.BackupKey, base64: false);
        return null;
    }

    private static async Task<IResult> StartAsync(Vm vm, HttpContext http, ProvisionJobs provisioning, ProvisionSubmission submission, CancellationToken ct)
    {
        try
        {
            var (job, replayed) = await provisioning.SubmitAsync(vm, http.User.Actor(), submission, ct);
            CodedProblems.Audit(http, "vm.provision", vm.Owner, target: vm.Name, extra: $"kind={job.Kind}; job={job.Id}; replayed={replayed}");
            return Results.Accepted("/api/v1/jobs/" + job.Id, new VmJobAcceptedResponse(job.Id, job.Kind, replayed));
        }
        catch (ProvisionRequestException ex)
        {
            if (ex.Code == "maintenance") return await MaintenanceFilter.RefusedAsync(http);
            return CodedProblems.Create(ex.Status, ex.Code, ex.Message, extra: ex.JobId is null ? null : new() { ["jobId"] = ex.JobId });
        }
    }

    /// <summary>
    /// <c>POST /jobs/{id}/retry</c> (design 7.1): the same kind of job again for the same VM with its stored settings,
    /// for a failed <c>provision</c>, <c>reinstall</c> or <c>restore</c>. The job's own steps keep the reinstall and
    /// restore rules (the original backup, no restore run twice over the VM). Only the VM's latest such job can be
    /// retried, and once: a repeat answers the retry it started (<c>replayed</c>). A restore is sent its backup's key again.
    /// </summary>
    private static async Task<IResult> RetryAsync(string id, JobRetryRequest? request, HttpContext http, IJobQueryStore history,
        IVmRepository vms, ProvisionJobs provisioning, CancellationToken ct)
    {
        var all = await history.ListAsync(ct);
        var failed = all.FirstOrDefault(j => j.Id == id);
        if (failed is null) return Problems.NotFound("Unknown job.");
        if (!http.User.IsAdmin() && !Ownership.SameName(http.User.NameOrEmpty(), failed.Owner)) return Problems.Forbidden("Job access refused.");
        if (!JobRetryRules.IsLifecycleKind(failed.Kind) || failed.VmName is null)
            return CodedProblems.Create(409, "not-retryable", $"A {failed.Kind} job cannot be retried.");
        if (failed.State != JobState.Failed) return CodedProblems.Create(409, "job-not-failed", "Only a failed job can be retried.");
        var vm = await vms.GetAsync(failed.VmName, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (JobRetryRules.Refusal(failed, vm, []) is { Code: "vm-changed" } changed) return CodedProblems.Create(changed.Status, changed.Code, changed.Message);
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;

        var requestId = JobRetryRules.RequestId(failed);
        if (JobRetryRules.Refusal(failed, vm, all) is { } refused) return CodedProblems.Create(refused.Status, refused.Code, refused.Message);
        string? backupId = null;
        if (failed.Kind == JobKinds.Restore)
        {
            backupId = failed.Result is null ? null : JsonSerializer.SerializeToElement(failed.Result, ApiJson.Options) is { ValueKind: JsonValueKind.Object } result
                && result.TryGetProperty("backupId", out var value) ? value.GetString() : null;
            // A restore cut off before it ran (an engine restart) has no result; the caller names the backup again.
            backupId ??= request?.BackupId;
            if (!BackupScript.IsBackupId(backupId)) return CodedProblems.Validation("backupId", "The failed restore does not name its backup; send it in 'backupId'.");
            if (!BackupJobKey.IsIdentity(request?.Secrets?.BackupKey))
                return CodedProblems.Validation("secrets.backupKey", "A restore needs the backup's key (AGE-SECRET-KEY-1...).");
        }
        if (SecretBag(request?.Secrets, failed.Kind == JobKinds.Restore, out var bag) is { } invalid) return invalid;
        return await StartAsync(vm, http, provisioning, new ProvisionSubmission(requestId, null, null, false, bag, failed.Kind, backupId), ct);
    }

    private static async Task<(Vm? Vm, IResult? Denied)> PrimaryAsync(string name, HttpContext http, IVmRepository vms, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return (null, Problems.NotFound("Unknown VM."));
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return (null, denied);
        if (vm.Kind != VmKind.Primary) return (null, LifecycleEndpoints.Problem("not-a-primary"));
        return (vm, null);
    }

    /// <summary><c>GET /vms/{name}/desired</c>: the settings the next provision uses; all null before any are stored.</summary>
    private static async Task<IResult> GetDesiredAsync(string name, HttpContext http, IVmRepository vms, IVmDesiredStateSource desired, CancellationToken ct)
    {
        var (vm, denied) = await PrimaryAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        return Results.Ok(VmDesiredStateResponse.From(await desired.GetDesiredForAsync(vm, ct)));
    }

    /// <summary>
    /// <c>PUT /vms/{name}/desired</c>: stores the settings for the next provision and changes nothing in the VM;
    /// <c>provision-status</c> then reports that a change waits (design 7.5).
    /// </summary>
    private static async Task<IResult> SetDesiredAsync(string name, VmDesiredState body, HttpContext http, IVmRepository vms,
        IVmDesiredStateSource desired, IClock clock, CancellationToken ct)
    {
        var (vm, denied) = await PrimaryAsync(name, http, vms, ct);
        if (vm is null) return denied!;
        if (vm.Deleting) return LifecycleEndpoints.Problem("vm-deleting");
        if (Validate(body) is { } field) return CodedProblems.Validation(field["desired.".Length..], "Invalid value.");
        var state = new StoredDesiredState(body, ProvisionScript.DesiredHash(body), clock.UtcNow, http.User.Actor(), vm.Created);
        await desired.SetDesiredAsync(vm.Name, state, ct);
        http.SetAuditDetail("hash=" + state.Hash[..12]);
        return Results.Ok(VmDesiredStateResponse.From(state));
    }

    /// <summary>Only what reaches a shell needs a pattern; every other value is single-quoted there.</summary>
    internal static string? Validate(VmDesiredState? desired)
    {
        if (desired is null) return null;
        if (desired.Settings is null) return "desired.settings";
        if (desired.Projects is null || desired.Projects.Any(p => p is null || !ProjectPattern().IsMatch(p))) return "desired.projects";
        if (desired.Settings.SeedUser is { } user && !UserPattern().IsMatch(user)) return "desired.settings.seedUser";
        if (desired.Settings.ExternalSshPort is < 1 or > 65535) return "desired.settings.externalSshPort";
        if (desired.Settings.GitCloneSkipHostsB64 is { } skip && !Base64Pattern().IsMatch(skip)) return "desired.settings.gitCloneSkipHostsB64";
        return null;
    }

    private static async Task<IResult> StatusAsync(string name, HttpContext http, IVmRepository vms, IVmDesiredStateSource desired,
        IVmAppliedStateStore applied, IReleaseInfo release, PrimaryCpuSettings cpu, PrimaryMemorySettings memory, CancellationToken ct)
    {
        var vm = await vms.GetAsync(name, ct);
        if (vm is null) return Problems.NotFound("Unknown VM.");
        if (await LifecycleEndpoints.AuthorizeAsync(vm, http, true, ct) is { } denied) return denied;
        if (vm.Kind != VmKind.Primary) return LifecycleEndpoints.Problem("not-a-primary");
        var want = await desired.GetDesiredForAsync(vm, ct);
        var have = await applied.GetAppliedAsync(vm.Name, ct);
        var available = SourceZipRules.ValidCommit(release.Installed.Commit) ? release.Installed.Commit : null;
        var cpuSetting = await cpu.GetAsync(vm, ct);
        var memorySetting = await memory.GetAsync(vm, ct);
        int? pendingCpu = cpuSetting is not null && cpuSetting.Cpus != vm.Cpu ? cpuSetting.Cpus : null;
        int? pendingRam = memorySetting is not null && memorySetting.RamGb != vm.RamGb ? memorySetting.RamGb : null;
        return Results.Ok(new ProvisionStatusResponse(
            // Stored settings the guest has not been provisioned with yet wait too, also before the first run.
            SettingsChanged: want is not null && want.Hash != have?.DesiredHash,
            NewerPayload: available is not null && have?.Commit is { } commit && !string.Equals(commit, available, StringComparison.OrdinalIgnoreCase),
            HardwareChanged: pendingCpu is not null || pendingRam is not null,
            want?.Hash, available, have, pendingCpu, pendingRam));
    }
}
