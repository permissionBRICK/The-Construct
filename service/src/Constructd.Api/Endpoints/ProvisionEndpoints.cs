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
        var secrets = request.Secrets ?? new ProvisionSecretsRequest();
        foreach (var (field2, value) in new[] { ("secrets.vmTokenB64", secrets.VmTokenB64), ("secrets.gitCloneCredentialsB64", secrets.GitCloneCredentialsB64), ("secrets.serveWebTokenB64", secrets.ServeWebTokenB64) })
            if (value is not null && !Base64Pattern().IsMatch(value)) return CodedProblems.Validation(field2, "Expected base64.");
        if (secrets.SudoPassword is { } pw && pw.Any(c => c is '\n' or '\r' or '\0')) return CodedProblems.Validation("secrets.sudoPassword", "Expected one line.");

        var bag = new JobSecretBag();
        bag.Set(ProvisionScript.VmTokenVariable, secrets.VmTokenB64);
        bag.Set(ProvisionScript.CloneCredentialsVariable, secrets.GitCloneCredentialsB64);
        bag.Set(ProvisionScript.ServeWebTokenVariable, secrets.ServeWebTokenB64);
        bag.Set(ProvisionJob.SudoPasswordSecret, secrets.SudoPassword, base64: false);
        if (request.Kind == JobKinds.Restore) bag.Set(ProvisionJob.BackupKeySecret, secrets.BackupKey, base64: false);
        try
        {
            var (job, replayed) = await provisioning.SubmitAsync(vm, http.User.Actor(),
                new ProvisionSubmission(request.RequestId, request.Commit, request.Desired, request.DeferProjectCommands == true, bag,
                    request.Kind, request.Kind == JobKinds.Restore ? request.BackupId : null, request.FreshImage == true), ct);
            CodedProblems.Audit(http, "vm.provision", vm.Owner, target: vm.Name, extra: $"kind={job.Kind}; job={job.Id}; replayed={replayed}");
            return Results.Accepted("/api/v1/jobs/" + job.Id, new VmJobAcceptedResponse(job.Id, job.Kind, replayed));
        }
        catch (ProvisionRequestException ex)
        {
            if (ex.Code == "maintenance") return await MaintenanceFilter.RefusedAsync(http);
            return CodedProblems.Create(ex.Status, ex.Code, ex.Message, extra: ex.JobId is null ? null : new() { ["jobId"] = ex.JobId });
        }
    }

    /// <summary>Only what reaches a shell needs a pattern; every other value is single-quoted there.</summary>
    private static string? Validate(VmDesiredState? desired)
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
        var want = await desired.GetDesiredAsync(vm.Name, ct);
        var have = await applied.GetAppliedAsync(vm.Name, ct);
        var available = SourceZipRules.ValidCommit(release.Installed.Commit) ? release.Installed.Commit : null;
        var cpuSetting = await cpu.GetAsync(vm, ct);
        var memorySetting = await memory.GetAsync(vm, ct);
        int? pendingCpu = cpuSetting is not null && cpuSetting.Cpus != vm.Cpu ? cpuSetting.Cpus : null;
        int? pendingRam = memorySetting is not null && memorySetting.RamGb != vm.RamGb ? memorySetting.RamGb : null;
        return Results.Ok(new ProvisionStatusResponse(
            SettingsChanged: want is not null && have is not null && want.Hash != have.DesiredHash,
            NewerPayload: available is not null && have?.Commit is { } commit && !string.Equals(commit, available, StringComparison.OrdinalIgnoreCase),
            HardwareChanged: pendingCpu is not null || pendingRam is not null,
            want?.Hash, available, have, pendingCpu, pendingRam));
    }
}
