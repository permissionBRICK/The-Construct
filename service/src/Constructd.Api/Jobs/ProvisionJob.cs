using Constructd.Api.Source;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>A provision step failed. The message is a code plus a safe explanation; never a value.</summary>
public sealed class ProvisionStepException(string step, string code, string message) : Exception($"{code}: {message}"), IConstructdError
{
    public string Step { get; } = step;
    public string Code { get; } = code;
}

/// <summary>One run of the <c>provision</c> job: everything it needs, the secrets in their bag.</summary>
public sealed record ProvisionRun(
    string JobId,
    Vm Vm,
    SshTarget Target,
    string Commit,
    StoredDesiredState Desired,
    string? Channel,
    bool DeferProjectCommands,
    string DefaultSeedUser,
    string? BootstrapPublicKey,
    JobSecretBag Secrets,
    ProvisionRestore? Restore = null);

/// <summary>
/// A backup to restore after <c>provision.sh</c> and before the project commands (the PowerShell's
/// <c>-RestoreHostBackupId</c>). Its key is <see cref="ProvisionJob.BackupKeySecret"/> in the bag.
/// </summary>
public sealed record ProvisionRestore(string BackupId);

/// <summary>
/// The <c>provision</c> job (design 7.3): the guest steps of <c>Provision-AgentVM.ps1</c>, run by the
/// engine. Steps, in order: <c>source</c> (the cached release source as the PowerShell's archive),
/// <c>copy</c> (upload and unpack), <c>provision</c> (<c>bin/provision.sh</c> with the PowerShell's
/// environment), <c>project-commands</c> (the deferred phase, only when the run deferred it),
/// <c>restore</c> (only with a backup to restore: <c>construct backup restore</c>, the key on stdin),
/// <c>applied</c> (read <c>/etc/construct/provisioned.env</c>), <c>finish</c> (the PowerShell's reboot
/// rule). Each step is safe to run again, so a failed job is retried from the start.
///
/// Cancellation is honoured between steps. Secrets reach the guest only on stdin, into a 0600 file
/// the root shell sources and removes; every line is passed through the bag's filter before it is
/// reported, so nothing that is stored can hold one.
/// </summary>
public sealed class ProvisionJob(ISshRunner ssh, ISourceCache sources, IVmAppliedStateStore applied,
    IPersistedJobRunner phases, RecoveryMarks marks, IClock clock)
{
    public static readonly string[] Steps = ["source", "copy", "provision", "restore", "project-commands", "applied", "finish"];
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProvisionTimeout = TimeSpan.FromHours(4);
    private static readonly string[] SecretVariables =
        [ProvisionScript.VmTokenVariable, ProvisionScript.CloneCredentialsVariable, ProvisionScript.ServeWebTokenVariable];
    public const string SudoPasswordSecret = "sudoPassword";
    /// <summary>The backup key <c>E</c> (<c>AGE-SECRET-KEY-1…</c>) of the backup a run restores.</summary>
    public const string BackupKeySecret = "backupKey";

    public async Task<JobOutcome> RunAsync(ProvisionRun run, IProgress<string> progress, CancellationToken cancel)
    {
        var context = new Context(run, progress);
        var seedUser = run.Desired.Desired.Settings.SeedUser ?? run.DefaultSeedUser;
        var errors = new List<ProvisionStepError>();
        string? payloadHash = null;
        string? tarPath = null;
        try
        {
            // ── source ───────────────────────────────────────────────────────────────
            await BeginAsync(context, "source", cancel);
            var item = await sources.EnsureAsync(run.Commit, new LineProgress(line => context.Line(line)), CancellationToken.None, run.JobId);
            payloadHash = item.Sha256;
            tarPath = Path.Combine(Path.GetTempPath(), $"construct-provision-{run.JobId}.tar.gz");
            var open = await sources.OpenAsync(run.Commit, CancellationToken.None);
            await using (open.Stream) await ProvisionSourceArchive.WriteTarGzAsync(open.Stream, run.Commit, tarPath, CancellationToken.None);
            context.Done($"source {run.Commit[..12]} ({item.SizeBytes} bytes, sha256 {item.Sha256[..12]})");

            // ── copy ─────────────────────────────────────────────────────────────────
            await BeginAsync(context, "copy", cancel);
            await RootAsync(context, "copy", "rm -f " + ProvisionScript.RemoteArchive, required: true);
            try { await ssh.CopyAsync(run.Target, tarPath, ProvisionScript.RemoteArchive, CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new ProvisionStepException("copy", "copy-failed", SafeError.Describe(ex)); }
            await RootAsync(context, "copy", ProvisionScript.UnpackCommand(seedUser), required: true);
            context.Done("repo in place at " + ProvisionScript.RepoDir);

            // ── provision ────────────────────────────────────────────────────────────
            await BeginAsync(context, "provision", cancel);
            var input = EnvInput(run, payloadHash);
            var main = await GuestScriptAsync(context, "provision", ProvisionScript.ProvisionCommand(ProvisionScript.MainEnvironment(input)),
                ProvisionScript.MainEnvironment(input), SecretVariables);
            var result = ProvisionScript.ParseResult(main.Lines);
            if (!result.IsValid)
                throw new ProvisionStepException("provision", "provision-result-missing", $"provision.sh did not report a valid result block (exit {main.ExitCode}).");
            errors.AddRange(result.Errors.Select(Redacted));
            if (main.ExitCode == 3 && result.ErrorCount == 0)
                throw new ProvisionStepException("provision", "provision-result-inconsistent", "provision.sh exited 3 but reported no failed step.");
            if (main.ExitCode is not (0 or 3))
                throw new ProvisionStepException("provision", "provision-critical-failure", $"provision.sh failed in a critical step (exit {main.ExitCode}).");
            if (main.ExitCode == 0 && result.ErrorCount != 0)
                throw new ProvisionStepException("provision", "provision-result-inconsistent", $"provision.sh exited 0 but reported {result.ErrorCount} failure(s).");
            context.Done(errors.Count == 0 ? "provisioning finished cleanly" : $"provisioning reached the end with {errors.Count} error(s)");

            // ── restore ──────────────────────────────────────────────────────────────
            if (run.Restore is { } restore)
            {
                await BeginAsync(context, "restore", cancel);
                await RestoreStepAsync(context, restore);
            }

            // ── project-commands ─────────────────────────────────────────────────────
            await BeginAsync(context, "project-commands", cancel);
            if (run.DeferProjectCommands)
            {
                var phaseVars = ProvisionScript.ProjectPhaseEnvironment(input);
                var phase = await GuestScriptAsync(context, "project-commands", ProvisionScript.ProvisionCommand(phaseVars), phaseVars,
                    [ProvisionScript.CloneCredentialsVariable]);
                var phaseResult = ProvisionScript.ParseResult(phase.Lines);
                // Invoke-DeferredProjectCommands: an unreadable result or a hard exit counts as one failure.
                errors.AddRange(!phaseResult.IsValid || phase.ExitCode is not (0 or 3)
                    ? [new ProvisionStepError(ProvisionScript.ProjectCommandsTitle, phase.ExitCode, "")]
                    : phaseResult.Errors.Select(Redacted));
                context.Done("project provisioning commands finished");
            }
            else
            {
                context.Report("project-commands", "skipped", "project commands ran inside the provision step");
            }

            // ── applied ──────────────────────────────────────────────────────────────
            await BeginAsync(context, "applied", cancel);
            var appliedState = await ReadAppliedAsync(context, run);
            context.Done(appliedState is null ? "the guest's provisioned.env could not be read" : "applied state recorded");

            // ── finish ───────────────────────────────────────────────────────────────
            await BeginAsync(context, "finish", cancel);
            var rebooted = await FinishAsync(context, run, seedUser);
            context.Done(rebooted ? "the VM reboots in a few seconds" : "the VM keeps running");

            var outcome = Result(run, payloadHash, errors, null, null, rebooted, appliedState);
            if (errors.Count > 0)
                throw new JobFailureException($"provision-steps-failed: {errors.Count} guest step(s) failed.", outcome);
            return new JobOutcome(outcome);
        }
        catch (ProvisionStepException ex)
        {
            context.Report(ex.Step, "failed", context.Secrets.Redact(ex.Message));
            throw new JobFailureException(context.Secrets.Redact(ex.Message),
                Result(run, payloadHash, errors, ex.Step, ex.Code, false, null));
        }
        catch (SourceException ex)
        {
            context.Report("source", "failed", ex.Code);
            throw new JobFailureException("source-unavailable: " + ex.Code, Result(run, payloadHash, errors, "source", "source-unavailable", false, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JobFailureException)
        {
            // An SSH timeout, a store failure: the step and what the guest reported so far stay in the result.
            var safe = context.Secrets.Redact(SafeError.Describe(ex));
            context.Report(context.Step, "failed", safe);
            throw new JobFailureException($"{context.Step}-failed: {safe}", Result(run, payloadHash, errors, context.Step, context.Step + "-failed", false, null));
        }
        finally
        {
            if (tarPath is not null) try { File.Delete(tarPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            run.Secrets.Clear();
        }

        ProvisionStepError Redacted(ProvisionStepError e) =>
            e with { Title = run.Secrets.Redact(e.Title), LogPath = run.Secrets.Redact(e.LogPath) };
    }

    /// <summary>
    /// The <c>restore</c> job (design 7.1): one chosen stored backup into the running VM, as the
    /// client's <c>backup-restore</c> action does. The key came from a device, in the bag.
    /// </summary>
    public async Task<JobOutcome> RestoreOnlyAsync(ProvisionRun run, IProgress<string> progress, CancellationToken cancel)
    {
        var context = new Context(run, progress) { Step = "restore" };
        var restore = run.Restore ?? throw new InvalidOperationException("A restore run names its backup.");
        try
        {
            await BeginAsync(context, "restore", cancel);
            await RestoreStepAsync(context, restore);
            return new JobOutcome(new RestoreJobResult(restore.BackupId, true, null, null, null));
        }
        catch (ProvisionStepException ex)
        {
            var safe = context.Secrets.Redact(ex.Message);
            context.Report(ex.Step, "failed", safe);
            throw new JobFailureException(safe, new RestoreJobResult(restore.BackupId, false, ex.Step, ex.Code, safe));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JobFailureException)
        {
            var safe = context.Secrets.Redact(SafeError.Describe(ex));
            context.Report(context.Step, "failed", safe);
            throw new JobFailureException($"restore-failed: {safe}", new RestoreJobResult(restore.BackupId, false, context.Step, "restore-failed", safe));
        }
        finally { run.Secrets.Clear(); }
    }

    /// <summary>
    /// <c>construct backup restore --from-host ID</c> with the key as the first line of stdin: the guest
    /// downloads, decrypts and restores the backup, brings back its profiles, serve-web token and
    /// recipients, and tells the host. Run at most once over a VM (design 7.1): the start is recorded
    /// before the guest begins, and a restore of the same backup that started and never finished is
    /// not run again, because the VM may hold data written since. Only an exit that says the restore
    /// never touched the VM (a key, transfer or lock failure) clears the record.
    /// </summary>
    private async Task RestoreStepAsync(Context context, ProvisionRestore restore)
    {
        var run = context.Run;
        var key = run.Secrets.Get(BackupKeySecret);
        if (!BackupJobKey.IsIdentity(key))
            throw new ProvisionStepException("restore", "secret-missing", $"{BackupKeySecret}: the key of backup {restore.BackupId} is needed to restore it; a device that can open the backup supplies it.");
        var earlier = await marks.GetRestoreAsync(run.Vm, CancellationToken.None);
        if (earlier is { CompletedAt: null } && earlier.BackupId == restore.BackupId)
            throw new ProvisionStepException("restore", "restore-already-ran", ManualRestore(restore.BackupId, earlier.StartedAt));

        await marks.SetRestoreAsync(run.Vm, new RecoveryMarks.Restore(run.Vm.Created, restore.BackupId, run.JobId, clock.UtcNow, null), CancellationToken.None);
        GuestRun result;
        try
        {
            result = await RootCommandAsync(context, "restore", BackupScript.Restore(restore.BackupId), ProvisionTimeout, key + "\n");
        }
        catch (ProvisionStepException ex) when (ex.Code == "secret-missing")
        {
            // sudo refused before anything ran.
            await marks.SetRestoreAsync(run.Vm, null, CancellationToken.None);
            throw;
        }

        if (result.ExitCode != 0)
        {
            var untouched = BackupScript.RestoreLeftVmUntouched(result.ExitCode);
            if (untouched) await marks.SetRestoreAsync(run.Vm, null, CancellationToken.None);
            throw new ProvisionStepException("restore", "restore-failed", untouched
                ? $"restoring backup {restore.BackupId} failed before it changed the VM: {BackupScript.ExitMessage(result.ExitCode)}. The backup stays on the host; the restore can be run again."
                : $"restoring backup {restore.BackupId} failed: {BackupScript.ExitMessage(result.ExitCode)}; the guest's log is /var/log/construct/restore-config.log. "
                  + "The VM may be partly restored, so the engine does not run this restore again by itself.");
        }

        await marks.SetRestoreAsync(run.Vm, new RecoveryMarks.Restore(run.Vm.Created, restore.BackupId, run.JobId, clock.UtcNow, clock.UtcNow), CancellationToken.None);
        context.Done($"backup {restore.BackupId} restored");

        // A backup made for a reinstall carries that job's recipient in its recipient list, and the
        // restore brings it back: it comes off again, whichever job restores the backup.
        var listed = await RootCommandAsync(context with { Quiet = true }, "restore", BackupScript.RecipientsList, ShortTimeout);
        var jobKeys = listed.ExitCode == 0 ? BackupScript.JobRecipients(listed.Lines) : null;
        if (jobKeys is null)
            throw new ProvisionStepException("restore", "recipient-remove-failed",
                $"the backup is restored, but the VM's backup recipients could not be read (exit {listed.ExitCode}); remove any recipient labelled "
                + $"'{BackupJobKey.Label}' with: sudo construct backup recipients remove FINGERPRINT.");
        foreach (var fingerprint in jobKeys)
        {
            var removed = await RootCommandAsync(context, "restore", BackupScript.RecipientsRemove(fingerprint), ShortTimeout);
            if (removed.ExitCode != 0)
                throw new ProvisionStepException("restore", "recipient-remove-failed",
                    $"the backup is restored, but a reinstall job's backup key {fingerprint} could not be removed from the VM (exit {removed.ExitCode}); "
                    + $"remove it with: sudo construct backup recipients remove {fingerprint}.");
            context.Report("restore", "running", $"removed the reinstall job's backup key {fingerprint[..16]}... from the VM");
        }
    }

    /// <summary>What a person does when a restore started before and may have changed the VM.</summary>
    public static string ManualRestore(string backupId, DateTimeOffset startedAt) =>
        $"backup {backupId} was already being restored into this VM ({startedAt:u}) and that run did not finish. "
        + "It is not restored again automatically: the VM may hold data written since. Save what is needed from the VM, "
        + $"then restore it by hand on the VM (sudo construct backup restore --from-host {backupId}, with the key a device unwraps: "
        + $"Provision-AgentVM.ps1 -Action backup-restore -BackupId {backupId}).";

    /// <summary>One root command on the guest for another job's step (a reinstall's backup steps), with this job's redaction.</summary>
    public async Task<(int ExitCode, IReadOnlyList<string> Lines)> RunGuestAsync(ProvisionRun run, IProgress<string> progress, string step,
        string command, string? input = null)
    {
        var result = await RootCommandAsync(new Context(run, progress) { Step = step }, step, command, ProvisionTimeout, input);
        return (result.ExitCode, result.Lines);
    }

    /// <summary>Reports a step state for another job, through this job's redaction.</summary>
    public static void Report(ProvisionRun run, IProgress<string> progress, string step, string state, string? line) =>
        new Context(run, progress).Report(step, state, line);

    private static ProvisionEnvInput EnvInput(ProvisionRun run, string payloadHash) => new(
        run.Vm.Name, run.Desired.Desired, run.DefaultSeedUser, run.Commit, run.Target.IsRoot, run.DeferProjectCommands,
        SecretVariables.Where(run.Secrets.Has).ToHashSet(StringComparer.Ordinal), payloadHash, run.Desired.Hash, run.Channel);

    private static ProvisionJobResult Result(ProvisionRun run, string? payloadHash, List<ProvisionStepError> errors,
        string? failedStep, string? code, bool rebooted, VmAppliedState? appliedState)
    {
        var failed = errors.ToArray();
        return new(run.Commit, payloadHash ?? "", run.Desired.Hash, run.Channel, failed.Length == 0 && failedStep is null,
            failedStep, code ?? (failed.Length > 0 ? "provision-steps-failed" : null), failed, ProvisionScript.FixPrompt(failed), rebooted, appliedState);
    }

    private async Task BeginAsync(Context context, string step, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        context.Step = step;
        await phases.SetPhaseAsync(context.Run.JobId, step, CancellationToken.None);
        context.Report(step, "running", null);
    }

    private sealed record GuestRun(int ExitCode, IReadOnlyList<string> Lines);

    /// <summary>
    /// Runs a <c>provision.sh</c> call as root with its secrets staged first: written over stdin into a
    /// 0600 file in the guest's <c>/tmp</c> by the connecting user, sourced and removed by the root
    /// shell. The sudo password, when there is one, is the only stdin of the root command.
    /// </summary>
    private async Task<GuestRun> GuestScriptAsync(Context context, string step, string command,
        IReadOnlyList<KeyValuePair<string, string>> vars, IReadOnlyList<string> secretNames)
    {
        if (ProvisionScript.Invalid(vars) is { } bad)
            throw new ProvisionStepException(step, "settings-invalid", $"the setting {bad} holds a line break.");
        var secrets = context.Run.Secrets.Entries(secretNames);
        string? secretFile = null;
        if (secrets.Count > 0)
        {
            secretFile = "/tmp/.construct-provision-secrets." + Guid.NewGuid().ToString("n");
            var staged = await ssh.RunAsync(context.Run.Target, ProvisionScript.StageFileCommand(secretFile),
                ProvisionScript.SecretFile(secrets), context.LineSink(), ShortTimeout, CancellationToken.None);
            if (staged != 0) throw new ProvisionStepException(step, "secret-stage-failed", $"the secrets could not be staged on the guest (exit {staged}).");
        }

        return await RootCommandAsync(context, step, ProvisionScript.WithSecrets(secretFile, command), ProvisionTimeout);
    }

    /// <param name="input">More stdin for the command itself, after the sudo password (a key, one line).</param>
    private async Task<GuestRun> RootCommandAsync(Context context, string step, string command, TimeSpan timeout, string? input = null)
    {
        var sudoPassword = context.Run.Target.IsRoot ? null : context.Run.Secrets.Get(SudoPasswordSecret);
        var lines = new List<string>();
        var sink = context.LineSink(lines);
        var stdin = (sudoPassword is null ? "" : sudoPassword + "\n") + input;
        var exit = await ssh.RunAsync(context.Run.Target, ProvisionScript.AsRoot(command, context.Run.Target.IsRoot, sudoPassword is not null),
            stdin.Length == 0 ? null : stdin, sink, timeout, CancellationToken.None);
        if (exit != 0 && sudoPassword is null && !context.Run.Target.IsRoot && lines.Any(l => l.Contains("sudo: a password is required", StringComparison.Ordinal)))
            throw new ProvisionStepException(step, "secret-missing", $"{SudoPasswordSecret}: {context.Run.Target.User} needs a password for sudo on this guest.");
        if (exit == 255 && !lines.Any(l => l == ProvisionScript.ResultStart))
            throw new ProvisionStepException(step, "ssh-failed", $"the guest could not be reached over SSH as {context.Run.Target.User}@{context.Run.Target.Host}:{context.Run.Target.Port}.");
        return new(exit, lines);
    }

    private async Task<GuestRun> RootAsync(Context context, string step, string command, bool required, string? input = null)
    {
        var run = await RootCommandAsync(context, step, command, ShortTimeout, input);
        if (required && run.ExitCode != 0)
            throw new ProvisionStepException(step, step + "-failed", $"a guest command of this step failed (exit {run.ExitCode}).");
        return run;
    }

    private async Task<VmAppliedState?> ReadAppliedAsync(Context context, ProvisionRun run)
    {
        var read = await RootCommandAsync(context with { Quiet = true }, "applied", ProvisionScript.ReadMarkerCommand, ShortTimeout);
        if (read.ExitCode != 0) return null;
        var marker = ProvisionScript.ParseMarker(string.Join('\n', read.Lines));
        string? Key(string name) => marker.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
        var state = new VmAppliedState(Key("CONSTRUCT_COMMIT"), Key("CONSTRUCT_PAYLOAD_HASH"), Key("CONSTRUCT_DESIRED_HASH"),
            Key("CONSTRUCT_CHANNEL"), Key("INSTALLED_AT"), Key("REPROVISIONED_AT"), clock.UtcNow, run.JobId);
        await applied.SetAppliedAsync(run.Vm.Name, state, CancellationToken.None);
        return state;
    }

    /// <summary>
    /// The PowerShell's last step. The bootstrap path (not root) always reboots. The root path reboots
    /// only when the guest asks for it (<c>/var/run/reboot-required</c>) and strips a leftover bootstrap
    /// key either way. On the bootstrap path the key stays: removing it would lock the engine out until
    /// it has the VM's own key.
    /// </summary>
    private async Task<bool> FinishAsync(Context context, ProvisionRun run, string seedUser)
    {
        var reboot = !run.Target.IsRoot;
        if (!reboot)
        {
            var probe = await RootCommandAsync(context with { Quiet = true }, "finish", ProvisionScript.RebootRequiredCommand, ShortTimeout);
            reboot = probe.ExitCode == 0 && probe.Lines.Any(l => l.Trim() == "yes");
        }

        var key = run.Target.IsRoot ? run.BootstrapPublicKey : null;
        if (reboot)
        {
            await RootAsync(context, "finish", ProvisionScript.RebootCommand(key, seedUser), required: false);
        }
        else if (key is not null)
        {
            await RootAsync(context, "finish", ProvisionScript.StripBootstrapCommand(key, seedUser), required: false);
        }

        return reboot;
    }

    private sealed record Context(ProvisionRun Run, IProgress<string> Progress)
    {
        public string Step { get; set; } = "source";
        public bool Quiet { get; init; }
        public JobSecretBag Secrets => Run.Secrets;

        public void Report(string step, string state, string? line)
        {
            var text = line is null ? null : Secrets.Redact(line);
            if (Progress is IJobStepProgress steps) steps.Report(new JobStepProgress(step, state, text));
            else Progress.Report(text ?? $"{step}: {state}");
        }

        public void Line(string line) => Report(Step, "running", ProvisionScript.CleanLine(line));

        public void Done(string line) => Report(Step, "succeeded", line);

        /// <summary>Collects every guest line (cleaned) and reports it unless the call is quiet.</summary>
        public IProgress<string> LineSink(List<string>? collect = null)
        {
            // Secrets the guest makes itself and prints are not in the bag; this call's filter drops them.
            var filter = new GuestOutputFilter();
            return new LineProgress(raw =>
            {
                var line = ProvisionScript.CleanLine(raw);
                collect?.Add(line);
                if (!Quiet && filter.Pass(line) is { } shown) Report(Step, "running", shown);
            });
        }
    }

    /// <summary>A synchronous sink: <see cref="Progress{T}"/> would post to the pool and reorder lines.</summary>
    private sealed class LineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
