using System.Text;
using Constructd.Api.Backups;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>One run of the <c>reinstall</c> job.</summary>
public sealed record ReinstallRun(
    string JobId,
    Vm Vm,
    string Commit,
    StoredDesiredState Desired,
    string? Channel,
    string DefaultSeedUser,
    bool FreshImage,
    JobSecretBag Secrets);

/// <summary>
/// The <c>reinstall</c> job (design 7.1, 10.2 "Reinstall without a device", S29). Steps, in order:
/// <list type="bullet">
/// <item><c>backup</c>: a key pair for this job, its public half enrolled on the VM as one more backup
/// recipient, a host request for a <c>pre-reinstall</c> backup and the guest's
/// <c>construct backup create</c>, which consumes it (so the backup is host-requested and pinned); the
/// job unwraps the backup's key with its private half and has the VM verify the backup with it.</item>
/// <item><c>delete</c>: the hypervisor VM goes; its registry record, forwards and settings stay.</item>
/// <item><c>create</c>: the same VM again through the driver (<see cref="VmJobs.BuildMachineAsync"/>), a
/// new VM token, and the engine forgets the old host key of the address.</item>
/// <item>the <c>provision</c> job's own steps with the stored settings, a <c>restore</c> of the backup
/// after <c>provision.sh</c>, which also removes the job's recipient (it comes back with the backup) from the
/// new VM, and the project
/// commands after it.</item>
/// </list>
/// The private half lives in this job's secret bag. It is discarded when the job ends, except after a
/// failure past the delete: then <see cref="ReinstallKeys"/> keeps it in memory for a while so a retry
/// can still restore. A failure before the delete leaves the VM as it was and removes the job's recipient
/// from it again.
///
/// Retries (design 7.1): every attempt records in <see cref="RecoveryMarks"/> whether the VM was deleted and
/// which backup the reinstall relies on. A retry after the delete starts at <c>create</c> with the same
/// backup and never makes a new one; without the key (the engine restarted) it rebuilds and provisions
/// the VM and ends with <c>restore-needs-device</c>, naming the backup a device restores with a
/// <c>restore</c> job. Once the restore started, a retry stops instead of deleting the VM again.
/// </summary>
public sealed class ReinstallJob(ProvisionJob provision, IProvisionTargets targets, BackupCoordinator backups,
    IHypervisorDriver driver, IVmNestedDriver nested, IVmRepository vms, IServiceScopeFactory scopes, RecoveryMarks marks,
    ReinstallKeys keys, IPersistedJobRunner phases, IClock clock)
{
    public static readonly string[] Steps = ["backup", "delete", "create"];
    public const string JobKeySecret = "jobKey";

    private sealed class State
    {
        public string Step = "backup";
        public string? BackupId;
        public string? Fingerprint;
        public string? PrivatePem;
        public bool RecipientAdded;
        public bool Deleted;
        public bool NestedForCreate;
        public bool RestoreStarted;
        public bool Restored;
        /// <summary>The run the guest commands go through: its bag redacts every line they print; no target until one is resolved.</summary>
        public ProvisionRun Guest = null!;
        public ProvisionJobResult? Provision;
    }

    public async Task<JobOutcome> RunAsync(ReinstallRun run, IProgress<string> progress, CancellationToken cancel)
    {
        var state = new State();
        var vm = run.Vm;
        var succeeded = false;
        state.Guest = Guest(run, vm, null!);
        try
        {
            var mark = await marks.GetReinstallAsync(vm, CancellationToken.None);
            var retry = mark is { DeletedAt: not null };
            if (retry)
            {
                await ResumeAsync(run, mark!, state, progress);
            }
            else
            {
                await BackupAsync(run, mark, state, progress, cancel);
                await DeleteAsync(run, state, progress, cancel);
            }

            var guest = state.Guest;

            // ── create ───────────────────────────────────────────────────────────────
            await BeginAsync(run, state, progress, "create", cancel);
            if (retry)
            {
                // Whatever an earlier attempt left under the name goes first: the backup was never restored into it.
                await driver.RemoveVmAsync(vm.Name, progress, CancellationToken.None);
            }

            var current = await vms.GetAsync(vm.Name, CancellationToken.None) ?? throw Fail("create", "vm-deleting", "The VM's record is gone.");
            var descriptor = new VmDescriptor(vm.Name, current.Cpu, current.RamGb, current.DiskGb, null, state.NestedForCreate, false);
            string token;
            await using (var scope = scopes.CreateAsyncScope())
            {
                (_, token) = await VmJobs.BuildMachineAsync(scope.ServiceProvider, descriptor, current, run.FreshImage, () => { },
                    new StepLines(guest, progress, "create"), CancellationToken.None);
            }

            run.Secrets.Set(ProvisionScript.VmTokenVariable, Convert.ToBase64String(Encoding.UTF8.GetBytes(token)));
            current = await vms.GetAsync(vm.Name, CancellationToken.None) ?? throw Fail("create", "vm-deleting", "The VM's record is gone.");
            var target = await targets.ResolveAsync(current, CancellationToken.None)
                ?? throw Fail("create", "vm-endpoint-unknown", "The new VM has no SSH endpoint the engine can dial.");
            // The rebuilt VM has a new host key; this is the one place that replaces the stored one.
            await targets.ForgetHostKeyAsync(target, CancellationToken.None);
            ProvisionJob.Report(guest, progress, "create", "succeeded", $"VM {vm.Name} created again at {target.Host}:{target.Port}");

            // ── provision, restore, project commands ─────────────────────────────────
            ProvisionRestore? restore = null;
            if (state.PrivatePem is not null)
            {
                restore = new ProvisionRestore(state.BackupId!);
                if (run.Secrets.Get(ProvisionJob.BackupKeySecret) is null)
                {
                    var backup = await backups.Store.GetAsync(state.BackupId!, CancellationToken.None);
                    var key = BackupJobKey.Unwrap(backup?.KeysJson, state.BackupId!, state.PrivatePem)
                        ?? throw Fail("restore", "backup-key-unreadable", $"backup {state.BackupId} no longer opens with the job's key; restore it from a device with a restore job.");
                    run.Secrets.Set(ProvisionJob.BackupKeySecret, key, base64: false);
                }
            }

            var provisionRun = new ProvisionRun(run.JobId, current, target, run.Commit, run.Desired, run.Channel, restore is not null,
                run.DefaultSeedUser, target.IsRoot ? targets.BootstrapPublicKey() : null, run.Secrets, restore);
            try
            {
                var outcome = await provision.RunAsync(provisionRun, progress, cancel);
                state.Provision = outcome.Result as ProvisionJobResult;
            }
            catch (JobFailureException ex)
            {
                state.Provision = ex.Result as ProvisionJobResult;
                if (state.BackupId is not null && await marks.GetRestoreAsync(current, CancellationToken.None) is { } restoreMark
                    && restoreMark.BackupId == state.BackupId)
                {
                    state.RestoreStarted = true;
                    state.Restored = restoreMark.CompletedAt is not null;
                }
                throw new ReinstallFailure(state.Provision?.FailedStep ?? "provision", state.Provision?.Code ?? "provision-failed", ex.Message);
            }

            state.Restored = restore is not null;
            succeeded = true;
            await marks.ClearReinstallAsync(vm, CancellationToken.None);
            if (restore is null)
            {
                // A retry without the key: the VM is back and provisioned, the restore waits for a device.
                var message = $"restore-needs-device: the VM is reinstalled and provisioned, but the job's key for backup {state.BackupId} is gone (the engine restarted). "
                    + $"A device restores it with a restore job for backup {state.BackupId}; the backup stays on the host.";
                ProvisionJob.Report(state.Guest, progress, "restore", "failed", message);
                throw new JobFailureException(message, Result(state, false, "restore", "restore-needs-device", message));
            }

            return new JobOutcome(Result(state, true, null, null, null));
        }
        catch (ProvisionStepException ex)
        {
            var safe = run.Secrets.Redact(ex.Message);
            ProvisionJob.Report(state.Guest, progress, ex.Step, "failed", safe);
            throw new JobFailureException($"{ex.Code}: {safe}", Result(state, false, ex.Step, ex.Code, safe));
        }
        catch (ReinstallFailure ex)
        {
            throw new JobFailureException(ex.Message, Result(state, false, ex.Step, ex.Code, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JobFailureException)
        {
            var safe = run.Secrets.Redact(SafeError.Describe(ex));
            ProvisionJob.Report(state.Guest, progress, state.Step, "failed", safe);
            throw new JobFailureException($"{state.Step}-failed: {safe}", Result(state, false, state.Step, state.Step + "-failed", safe));
        }
        finally
        {
            if (!succeeded) await AfterFailureAsync(run, state, progress);
            else keys.Drop(vm.Name);
            run.Secrets.Clear();
            state.PrivatePem = null;
        }
    }

    // ── backup ───────────────────────────────────────────────────────────────────

    private async Task BackupAsync(ReinstallRun run, RecoveryMarks.Reinstall? earlier, State state, IProgress<string> progress, CancellationToken cancel)
    {
        var vm = run.Vm;
        await BeginAsync(run, state, progress, "backup", cancel);
        if (!(await backups.ConfigAsync(CancellationToken.None)).Enabled)
            throw Fail("backup", "backups-disabled", "Config backups are disabled on this host; the VM is not deleted without a backup.");
        var target = await targets.ResolveAsync(vm, CancellationToken.None)
            ?? throw Fail("backup", "vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial; it is not deleted.");
        var guest = state.Guest = Guest(run, vm, target);

        // An earlier attempt that ended before the delete without removing its key (the engine died).
        if (earlier?.Fingerprint is { } stale)
        {
            var removed = await provision.RunGuestAsync(guest, progress, "backup", BackupScript.RecipientsRemove(stale));
            if (removed.ExitCode != 0)
                throw Fail("backup", "recipient-remove-failed", $"the backup key {stale} of an earlier reinstall could not be removed from the VM (exit {removed.ExitCode}).");
        }

        state.NestedForCreate = await nested.GetNestedAsync(vm.Name, CancellationToken.None);
        var (privatePem, publicPem, fingerprint) = BackupJobKey.Create();
        state.PrivatePem = privatePem;
        state.Fingerprint = fingerprint;
        run.Secrets.Set(JobKeySecret, privatePem, base64: false);
        await marks.SetReinstallAsync(vm, new RecoveryMarks.Reinstall(vm.Created, true, run.JobId, null, fingerprint, state.NestedForCreate, null), CancellationToken.None);

        state.RecipientAdded = true;
        var add = await provision.RunGuestAsync(guest, progress, "backup", BackupScript.RecipientsAdd(BackupJobKey.Label), publicPem);
        if (add.ExitCode != 0)
            throw Fail("backup", "recipient-add-failed", $"the job's backup key could not be enrolled on the VM: {BackupScript.ExitMessage(add.ExitCode)}.");
        if (!add.Lines.Any(l => l.Trim() == fingerprint))
            throw Fail("backup", "recipient-add-failed", "the VM did not report the job's key as enrolled.");

        await using (await backups.LockAsync(vm.Owner, vm.Name, CancellationToken.None))
            await backups.OpenRequestLockedAsync(vm.Owner, vm.Name, BackupTriggers.PreReinstall, CancellationToken.None);
        var create = await provision.RunGuestAsync(guest, progress, "backup", BackupScript.CreatePreReinstall);
        if (create.ExitCode != 0)
            throw Fail("backup", "backup-failed", $"the VM could not back itself up: {BackupScript.ExitMessage(create.ExitCode)}; it is not deleted.");
        var id = BackupScript.CreatedId(create.Lines)
            ?? throw Fail("backup", "backup-failed", "construct backup create did not report a backup id; the VM is not deleted.");
        state.BackupId = id;
        await marks.SetReinstallAsync(vm, new RecoveryMarks.Reinstall(vm.Created, true, run.JobId, id, fingerprint, state.NestedForCreate, null), CancellationToken.None);

        // Only a backup that answered the host's request is pinned and protected; a slot upload is not kept.
        var backup = await backups.Store.GetAsync(id, CancellationToken.None);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsComplete || backup.IsSlot || backup.Trigger != BackupTriggers.PreReinstall)
            throw Fail("backup", "backup-not-protected", $"backup {id} is not stored as this VM's pre-reinstall backup; the VM is not deleted.");
        var key = BackupJobKey.Unwrap(backup.KeysJson, id, privatePem)
            ?? throw Fail("backup", "backup-key-unreadable", $"backup {id} does not open with the job's key; the VM is not deleted.");
        run.Secrets.Set(ProvisionJob.BackupKeySecret, key, base64: false);
        var verify = await provision.RunGuestAsync(guest, progress, "backup", BackupScript.Verify(id), key + "\n");
        if (verify.ExitCode != 0)
            throw Fail("backup", "backup-verify-failed", $"backup {id} did not verify on the VM: {BackupScript.ExitMessage(verify.ExitCode)}; the VM is not deleted.");
        ProvisionJob.Report(guest, progress, "backup", "succeeded", $"pre-reinstall backup {id} made and verified");
    }

    private async Task DeleteAsync(ReinstallRun run, State state, IProgress<string> progress, CancellationToken cancel)
    {
        var vm = run.Vm;
        var guest = state.Guest;
        await BeginAsync(run, state, progress, "delete", cancel);
        // Recorded before the delete: from here on a retry starts at create and keeps this backup.
        await marks.SetReinstallAsync(vm, new RecoveryMarks.Reinstall(vm.Created, true, run.JobId, state.BackupId, state.Fingerprint,
            state.NestedForCreate, clock.UtcNow), CancellationToken.None);
        state.Deleted = true;
        await driver.RemoveVmAsync(vm.Name, new StepLines(guest, progress, "delete"), CancellationToken.None);
        ProvisionJob.Report(guest, progress, "delete", "succeeded", $"VM {vm.Name} deleted; its record, forwards and settings stay");
    }

    /// <summary>A retry after the delete: the same backup, the key if this engine still holds it, and no second restore.</summary>
    private async Task ResumeAsync(ReinstallRun run, RecoveryMarks.Reinstall mark, State state, IProgress<string> progress)
    {
        var vm = run.Vm;
        var guest = state.Guest;
        state.Deleted = true;
        state.BackupId = mark.BackupId;
        state.Fingerprint = mark.Fingerprint;
        state.NestedForCreate = mark.Nested;
        await phases.SetPhaseAsync(run.JobId, "backup", CancellationToken.None);
        if (mark.BackupId is null)
            throw Fail("backup", "backup-missing", "an earlier attempt deleted the VM without a backup id on record; nothing is restored automatically.");
        if (await marks.GetRestoreAsync(vm, CancellationToken.None) is { } restore && restore.BackupId == mark.BackupId)
            throw Fail("restore", "restore-already-ran",
                $"backup {mark.BackupId} was already restored (or partly restored) into the new VM ({restore.StartedAt:u}); the VM is not deleted again. "
                + "Reprovision it (a provision job) to finish, and save anything needed from it before restoring by hand.");
        var backup = await backups.Store.GetAsync(mark.BackupId, CancellationToken.None);
        if (backup is null || !backup.IsComplete)
            throw Fail("backup", "backup-missing", $"backup {mark.BackupId}, which this reinstall relies on, is no longer on the host.");

        state.PrivatePem = keys.Take(vm.Name, mark.BackupId);
        if (state.PrivatePem is not null) run.Secrets.Set(JobKeySecret, state.PrivatePem, base64: false);
        ProvisionJob.Report(guest, progress, "backup", "skipped", state.PrivatePem is not null
            ? $"retry: an earlier attempt made backup {mark.BackupId} and deleted the VM; it is restored with the job's key"
            : $"retry: an earlier attempt made backup {mark.BackupId} and deleted the VM; the job's key is gone, so the restore will need a device");
        ProvisionJob.Report(guest, progress, "delete", "skipped", "the VM was deleted by an earlier attempt");
    }

    // ── failure ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Before the delete: the VM survives, so the job's recipient comes off it again and the key is
    /// discarded; when that cannot be done, the mark keeps the fingerprint for the next attempt. After
    /// the delete: the mark stays and the key waits in memory for a retry.
    /// </summary>
    private async Task AfterFailureAsync(ReinstallRun run, State state, IProgress<string> progress)
    {
        var vm = run.Vm;
        var guest = state.Guest;
        try
        {
            if (state.Deleted)
            {
                if (state.PrivatePem is not null && state.BackupId is not null && !state.RestoreStarted) keys.Hold(vm.Name, state.BackupId, state.PrivatePem);
                else keys.Drop(vm.Name);
                return;
            }

            keys.Drop(vm.Name);
            if (state.RecipientAdded && state.Fingerprint is { } fingerprint && guest.Target is not null)
            {
                var removed = await provision.RunGuestAsync(guest, progress, "backup", BackupScript.RecipientsRemove(fingerprint));
                if (removed.ExitCode != 0)
                {
                    ProvisionJob.Report(guest, progress, "backup", "failed",
                        $"the job's backup key {fingerprint} could not be removed from the VM (exit {removed.ExitCode}); the next reinstall removes it");
                    return;
                }

                ProvisionJob.Report(guest, progress, "backup", "failed", "the VM is unchanged; the job's backup key is removed from it again");
            }

            await marks.ClearReinstallAsync(vm, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProvisionJob.Report(guest, progress, state.Step, "failed", "cleanup after the failure: " + run.Secrets.Redact(SafeError.Describe(ex)));
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private async Task BeginAsync(ReinstallRun run, State state, IProgress<string> progress, string step, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        state.Step = step;
        await phases.SetPhaseAsync(run.JobId, step, CancellationToken.None);
        ProvisionJob.Report(state.Guest, progress, step, "running", null);
    }

    /// <summary>A provision run for the guest commands of the backup steps (no source, no settings used).</summary>
    private static ProvisionRun Guest(ReinstallRun run, Vm vm, SshTarget target) =>
        new(run.JobId, vm, target, run.Commit, run.Desired, run.Channel, false, run.DefaultSeedUser, null, run.Secrets);

    private static ReinstallJobResult Result(State state, bool succeeded, string? step, string? code, string? message) =>
        new(state.BackupId, succeeded, step, code, message, state.Deleted, state.Restored, state.Provision);

    private static ProvisionStepException Fail(string step, string code, string message) => new(step, code, message);

    private sealed class ReinstallFailure(string step, string code, string message) : Exception(message)
    {
        public string Step { get; } = step;
        public string Code { get; } = code;
    }

    /// <summary>Driver and creation lines, reported under the step with the bag's redaction.</summary>
    private sealed class StepLines(ProvisionRun guest, IProgress<string> progress, string step) : IProgress<string>
    {
        public void Report(string value) => ProvisionJob.Report(guest, progress, step, "running", value);
    }
}
