using Constructd.Api.Backups;
using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>Where the engine dials a VM to provision it.</summary>
public interface IProvisionTargets
{
    Task<SshTarget?> ResolveAsync(Vm vm, CancellationToken ct);

    /// <summary>The committed bootstrap public key, or null when the engine has none.</summary>
    string? BootstrapPublicKey();

    /// <summary>
    /// Drops the stored host key of <paramref name="target"/>'s address from the engine's known-hosts
    /// file, so the next connection accepts the new one. Only a job that just created the VM (a reinstall, or a
    /// create that provisions) calls this: everywhere else a changed host key stays a failure.
    /// </summary>
    Task ForgetHostKeyAsync(SshTarget target, CancellationToken ct);

    /// <summary>
    /// Forgets the old client's root key kept for an adopted VM, once that VM is gone (a reinstall's delete, a
    /// delete): a VM built again under the name is the engine's own, reached as the seed user.
    /// </summary>
    void ForgetAdoptedKey(string vm) { }
}

/// <summary>
/// The driver's endpoint, as the seed user (the configured account for an external VM), with the
/// bootstrap key next to <c>Iso:BootstrapPublicKeyPath</c> — the PowerShell's bootstrap path. A VM the
/// engine adopted from the old client has no seed user: it is reached as root with the old client's key
/// the adoption kept (<see cref="AdoptedKeyPath"/>); its first provision adds the bootstrap key for root.
/// </summary>
public sealed class ProvisionTargets(IHypervisorDriver driver, ConstructdOptions options) : IProvisionTargets
{
    public async Task<SshTarget?> ResolveAsync(Vm vm, CancellationToken ct)
    {
        if (await driver.GetEndpointAsync(vm.Name, ct) is not { } endpoint) return null;
        var known = Path.Combine(KeyDirectory(options), "known_hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(known)!);
        var listed = driver is ExternalEndpointDriver external ? external.Find(vm.Name) : null;
        var adopted = AdoptedKeyPath(options, vm.Name);
        if (listed is null && File.Exists(adopted)) return new SshTarget(endpoint.SshHost, endpoint.SshPort, "root", adopted, known);
        var pub = options.Iso.BootstrapPublicKeyPath;
        var key = pub.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && File.Exists(pub[..^4]) ? pub[..^4] : null;
        return new SshTarget(endpoint.SshHost, endpoint.SshPort, listed?.User ?? options.Iso.SeedUser, key, known);
    }

    /// <summary>Where the adoption keeps the old client's root key for a VM (in the engine's own data directory).</summary>
    public static string AdoptedKeyPath(ConstructdOptions options, string vm) => Path.Combine(KeyDirectory(options), vm + "_root");

    private static string KeyDirectory(ConstructdOptions options) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "ssh");

    public void ForgetAdoptedKey(string vm)
    {
        // File.Delete throws for a missing directory: an engine that never adopted a VM has none.
        var key = AdoptedKeyPath(options, vm);
        if (File.Exists(key)) File.Delete(key);
    }

    public string? BootstrapPublicKey()
    {
        var pub = options.Iso.BootstrapPublicKeyPath;
        return pub.Length > 0 && File.Exists(pub) ? File.ReadAllText(pub).Trim() : null;
    }

    public Task ForgetHostKeyAsync(SshTarget target, CancellationToken ct) => ForgetAsync(target, ct);

    /// <summary>Removes the stored host key of the target's address from its known_hosts file; the next connection learns the key the VM presents.</summary>
    public static async Task ForgetAsync(SshTarget target, CancellationToken ct)
    {
        if (target.KnownHostsFile is not { Length: > 0 } file || !File.Exists(file)) return;
        var text = await File.ReadAllTextAsync(file, ct);
        var kept = KnownHosts.Remove(text, target.Host, target.Port);
        if (kept == text) return;
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, kept, ct);
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>Why a provision request was refused before a job existed.</summary>
public sealed class ProvisionRequestException(int status, string code, string message, string? jobId = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? JobId { get; } = jobId;
}

/// <summary>
/// What a job request carries after validation (secrets already in their bag). <paramref name="Kind"/> is
/// <c>provision</c>, <c>reinstall</c> (<paramref name="FreshImage"/>: new install media) or <c>restore</c>
/// (<paramref name="BackupId"/>, its key in the bag). <paramref name="Device"/> is the device whose token asked for
/// the job (<see cref="JobDevices"/>), null for any other caller and for the engine itself.
/// </summary>
public sealed record ProvisionSubmission(string RequestId, string? Commit, VmDesiredState? Desired, bool DeferProjectCommands, JobSecretBag Secrets,
    string Kind = JobKinds.Provision, string? BackupId = null, bool FreshImage = false, string? Device = null);

/// <summary>
/// Accepts <c>provision</c>, <c>reinstall</c> and <c>restore</c> jobs (design 7.1): a repeated <c>requestId</c> returns the job it started,
/// one lifecycle job per VM (the VM's operation gate is held for the whole run), the desired state
/// saved before the run so a retry or a reprovision needs no client.
/// </summary>
public sealed class ProvisionJobs(IJobStore jobs, IJobQueryStore history, IPersistedJobRunner runner, IVmOperationGate gates,
    IMaintenanceGate maintenance, IVmDesiredStateSource desired, IProvisionTargets targets, IReleaseInfo release,
    IHostConfigStore config, ConstructdOptions options, ProvisionJob job, ReinstallJob reinstall, IHypervisorDriver driver,
    BackupCoordinator backups, JobDevices devices, IClock clock)
{
    // owner|vm|requestId → job id, the fast path; the stored jobs are the fallback.
    private readonly ConcurrentDictionary<string, string> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _submit = new(1, 1);

    public async Task<(Job Job, bool Replayed)> SubmitAsync(Vm vm, string actor, ProvisionSubmission request, CancellationToken ct)
    {
        var key = $"{vm.Owner}|{vm.Name}|{request.RequestId}";
        await _submit.WaitAsync(ct);
        try
        {
            // The request id is stored with the job, so a repeat after a restart finds it too.
            var existing = _requests.TryGetValue(key, out var existingId) ? await jobs.GetAsync(existingId, ct) : null;
            existing ??= (await history.ListAsync(ct)).FirstOrDefault(j => j.Kind is JobKinds.Provision or JobKinds.Reinstall or JobKinds.Restore
                && j.OperationKey == request.RequestId
                && Ownership.SameName(j.VmName, vm.Name) && Ownership.SameName(j.Owner, vm.Owner));
            if (existing is not null)
            {
                request.Secrets.Clear();
                return (existing, true);
            }

            var job = await StartAsync(vm, actor, request, ct);
            _requests[key] = job.Id;
            return (job, false);
        }
        catch
        {
            request.Secrets.Clear();
            throw;
        }
        finally { _submit.Release(); }
    }

    /// <summary>The Construct commit a provision uses (<paramref name="requested"/>, else the engine's installed one), or why there is none.</summary>
    public string SourceCommit(string? requested)
    {
        var commit = requested ?? release.Installed.Commit;
        if (!SourceZipRules.ValidCommit(commit))
            throw new ProvisionRequestException(409, "source-commit-unknown", "The engine does not know which Construct commit to provision; name one in 'commit'.");
        if (!options.HostAdmin.Source.Enabled)
            throw new ProvisionRequestException(409, "unsupported-capability", "The source cache is disabled on this host.");
        return commit;
    }

    /// <summary>
    /// The first provision of a VM, run inside the create job that just built it (the create request carried
    /// settings), so it does not depend on a client staying open. The settings are stored first, so a failed run
    /// is retried with a <c>provision</c> job. The new VM token reaches the guest as a provision secret. The caller
    /// holds the VM's operation gate. Throws <see cref="JobFailureException"/> with the provision result when it fails.
    /// </summary>
    /// <param name="secrets">The create request's secrets (clone credentials, sudo password), or none; the VM token joins them.</param>
    public async Task<ProvisionJobResult> ProvisionCreatedAsync(string jobId, Vm vm, VmDesiredState given, string actor, string vmToken,
        IProgress<string> progress, CancellationToken ct, JobSecretBag? secrets = null)
    {
        var state = new StoredDesiredState(given, ProvisionScript.DesiredHash(given), clock.UtcNow, actor, vm.Created);
        await desired.SetDesiredAsync(vm.Name, state, ct);
        string commit;
        try { commit = SourceCommit(null); }
        catch (ProvisionRequestException ex) { throw Refused("source", ex.Code, ex.Message); }
        var target = await targets.ResolveAsync(vm, ct) ?? throw Refused("copy", "vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial.");
        // A new VM: whatever key an earlier machine at its address had is not this one's.
        await targets.ForgetHostKeyAsync(target, ct);
        var channel = HostUpdateTrust.Settings(await config.GetAsync<UpdatesConfig>("updates", ct), options).Channel;
        var bag = secrets ?? new JobSecretBag();
        bag.Set(ProvisionScript.VmTokenVariable, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vmToken)));
        var outcome = await job.RunAsync(new ProvisionRun(jobId, vm, target, commit, state, channel, false, options.Iso.SeedUser,
            target.IsRoot ? targets.BootstrapPublicKey() : null, bag), progress, ct);
        return (ProvisionJobResult)outcome.Result!;

        JobFailureException Refused(string step, string code, string message)
        {
            progress.Report($"{step}: failed: {message}");
            return new JobFailureException($"{code}: {message}", new ProvisionJobResult("", "", state.Hash, null, false, step, code, [], [], false, null));
        }
    }

    private async Task<Job> StartAsync(Vm vm, string actor, ProvisionSubmission request, CancellationToken ct)
    {
        if (request.Kind == JobKinds.Restore) return await StartRestoreAsync(vm, actor, request, ct);
        if (request.Kind == JobKinds.Reinstall)
        {
            if (driver is ExternalEndpointDriver)
                throw new ProvisionRequestException(409, "unsupported-capability", "An external machine cannot be reinstalled by the engine.");
            if (!(await backups.ConfigAsync(ct)).Enabled)
                throw new ProvisionRequestException(409, "backups-disabled", "A reinstall keeps the VM's config in a host backup, and config backups are disabled on this host.");
        }

        var commit = SourceCommit(request.Commit);

        StoredDesiredState state;
        if (request.Desired is { } given)
        {
            state = new StoredDesiredState(given, ProvisionScript.DesiredHash(given), clock.UtcNow, actor, vm.Created);
        }
        else
        {
            state = await desired.GetDesiredForAsync(vm, ct)
                ?? throw new ProvisionRequestException(409, "desired-state-missing", "No settings are stored for this VM yet; send 'desired' once.");
        }

        // A reinstall finds its VM itself: a retry after the delete has none to dial yet.
        var target = request.Kind == JobKinds.Reinstall ? null : await targets.ResolveAsync(vm, ct)
            ?? throw new ProvisionRequestException(409, "vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial.");

        var id = Guid.NewGuid().ToString("n");
        var operation = await gates.TryAcquireAsync(vm.Name, id, ct);
        if (operation is null)
        {
            gates.IsHeld(vm.Name, out var holder);
            throw new ProvisionRequestException(409, "operation-in-progress", "Another operation runs on this VM.", holder);
        }

        IDisposable? handle = new GateHandle(operation, null);
        try
        {
            if (vm.CurrentJobId is { } current && await jobs.GetAsync(current, ct) is { State: JobState.Queued or JobState.Running })
                throw new ProvisionRequestException(409, "operation-in-progress", "Another job runs on this VM.", current);
            var entered = maintenance.TryEnter("mutation:" + request.Kind, id, vm.Name)
                ?? throw new ProvisionRequestException(503, "maintenance", "The host is in maintenance.");
            handle = new GateHandle(operation, entered);

            // Saved before the run: a retry, or a reprovision with no client, uses the same settings.
            if (request.Desired is not null) await desired.SetDesiredAsync(vm.Name, state, ct);
            var channel = HostUpdateTrust.Settings(await config.GetAsync<UpdatesConfig>("updates", ct), options).Channel;
            var queued = new Job(id, request.Kind, vm.Name, vm.Owner, JobState.Queued, [], null, null, clock.UtcNow, null,
                actor, request.RequestId, request.Kind == JobKinds.Reinstall ? ReinstallJob.Steps[0] : ProvisionJob.Steps[0]);
            await jobs.UpsertAsync(queued, ct);
            // Before it runs: a job that fails at once starts its repair from what is known here.
            devices.Remember(id, request.Device);
            Func<IProgress<string>, CancellationToken, Task<JobOutcome>> work = request.Kind == JobKinds.Reinstall
                ? (progress, token) => reinstall.RunAsync(new ReinstallRun(id, vm, commit, state, channel, options.Iso.SeedUser, request.FreshImage,
                    request.Secrets), progress, token)
                : (progress, token) => job.RunAsync(new ProvisionRun(id, vm, target!, commit, state, channel, request.DeferProjectCommands,
                    options.Iso.SeedUser, target!.IsRoot ? targets.BootstrapPublicKey() : null, request.Secrets), progress, token);
            await runner.StartPersistedAsync(queued, handle, work, CancellationToken.None);
            handle = null;
            return queued;
        }
        finally { handle?.Dispose(); }
    }

    /// <summary>
    /// A <c>restore</c> job: a stored backup of this VM into the running VM. The key comes from a device in
    /// the request's secrets; nothing else of the VM's settings is needed.
    /// </summary>
    private async Task<Job> StartRestoreAsync(Vm vm, string actor, ProvisionSubmission request, CancellationToken ct)
    {
        var backupId = request.BackupId ?? throw new ProvisionRequestException(400, "validation", "A restore names its backup.");
        var backup = await backups.Store.GetAsync(backupId, ct);
        if (backup is null || !BackupCoordinator.Belongs(backup, vm) || !backup.IsComplete)
            throw new ProvisionRequestException(404, "not-found", "No such complete backup of this VM.");
        var target = await targets.ResolveAsync(vm, ct)
            ?? throw new ProvisionRequestException(409, "vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial.");

        var id = Guid.NewGuid().ToString("n");
        var operation = await gates.TryAcquireAsync(vm.Name, id, ct);
        if (operation is null)
        {
            gates.IsHeld(vm.Name, out var holder);
            throw new ProvisionRequestException(409, "operation-in-progress", "Another operation runs on this VM.", holder);
        }

        IDisposable? handle = new GateHandle(operation, null);
        try
        {
            if (vm.CurrentJobId is { } current && await jobs.GetAsync(current, ct) is { State: JobState.Queued or JobState.Running })
                throw new ProvisionRequestException(409, "operation-in-progress", "Another job runs on this VM.", current);
            var entered = maintenance.TryEnter("mutation:" + JobKinds.Restore, id, vm.Name)
                ?? throw new ProvisionRequestException(503, "maintenance", "The host is in maintenance.");
            handle = new GateHandle(operation, entered);
            var queued = new Job(id, JobKinds.Restore, vm.Name, vm.Owner, JobState.Queued, [], null, null, clock.UtcNow, null,
                actor, request.RequestId, "restore");
            await jobs.UpsertAsync(queued, ct);
            devices.Remember(id, request.Device);
            // A restore uses no source: the run carries the VM, its address and the backup. A backup made for a reinstall
            // is restored into the VM built from it, whose project commands ran before the restore brought back what they
            // need (secrets, tokens): they run again after it, with the VM's stored settings (F39).
            var projects = backup.Trigger == BackupTriggers.PreReinstall;
            var settings = projects ? await desired.GetDesiredForAsync(vm, ct) : null;
            settings ??= new StoredDesiredState(new VmDesiredState([], new ProvisionSettings()), "", clock.UtcNow, actor);
            var run = new ProvisionRun(id, vm, target, "", settings, null, projects, options.Iso.SeedUser, null, request.Secrets,
                new ProvisionRestore(backupId));
            await runner.StartPersistedAsync(queued, handle, (progress, token) => job.RestoreOnlyAsync(run, progress, token), CancellationToken.None);
            handle = null;
            return queued;
        }
        finally { handle?.Dispose(); }
    }

    private sealed class GateHandle(IAsyncDisposable operation, IDisposable? maintenance) : IDisposable
    {
        public void Dispose()
        {
            try { maintenance?.Dispose(); }
            finally { operation.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }
}
