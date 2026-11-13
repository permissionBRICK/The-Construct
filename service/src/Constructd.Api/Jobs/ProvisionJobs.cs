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
}

/// <summary>
/// The driver's endpoint, as the seed user (the configured account for an external VM), with the
/// bootstrap key next to <c>Iso:BootstrapPublicKeyPath</c> — the PowerShell's bootstrap path. The
/// engine has no root key per VM yet (another block generates one at create time).
/// </summary>
public sealed class ProvisionTargets(IHypervisorDriver driver, ConstructdOptions options) : IProvisionTargets
{
    public async Task<SshTarget?> ResolveAsync(Vm vm, CancellationToken ct)
    {
        if (await driver.GetEndpointAsync(vm.Name, ct) is not { } endpoint) return null;
        var user = driver is ExternalEndpointDriver external && external.Find(vm.Name) is { } listed ? listed.User : options.Iso.SeedUser;
        var pub = options.Iso.BootstrapPublicKeyPath;
        var key = pub.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && File.Exists(pub[..^4]) ? pub[..^4] : null;
        var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        var known = Path.Combine(data, "ssh", "known_hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(known)!);
        return new SshTarget(endpoint.SshHost, endpoint.SshPort, user, key, known);
    }

    public string? BootstrapPublicKey()
    {
        var pub = options.Iso.BootstrapPublicKeyPath;
        return pub.Length > 0 && File.Exists(pub) ? File.ReadAllText(pub).Trim() : null;
    }
}

/// <summary>Why a provision request was refused before a job existed.</summary>
public sealed class ProvisionRequestException(int status, string code, string message, string? jobId = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? JobId { get; } = jobId;
}

/// <summary>What a provision request carries after validation (secrets already in their bag).</summary>
public sealed record ProvisionSubmission(string RequestId, string? Commit, VmDesiredState? Desired, bool DeferProjectCommands, JobSecretBag Secrets);

/// <summary>
/// Accepts <c>provision</c> jobs (design 7.1): a repeated <c>requestId</c> returns the job it started,
/// one lifecycle job per VM (the VM's operation gate is held for the whole run), the desired state
/// saved before the run so a retry or a reprovision needs no client.
/// </summary>
public sealed class ProvisionJobs(IJobStore jobs, IJobQueryStore history, IPersistedJobRunner runner, IVmOperationGate gates,
    IMaintenanceGate maintenance, IVmDesiredStateSource desired, IProvisionTargets targets, IReleaseInfo release,
    IHostConfigStore config, ConstructdOptions options, ProvisionJob job, IClock clock)
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
            existing ??= (await history.ListAsync(ct)).FirstOrDefault(j => j.Kind == JobKinds.Provision && j.OperationKey == request.RequestId
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

    private async Task<Job> StartAsync(Vm vm, string actor, ProvisionSubmission request, CancellationToken ct)
    {
        var commit = request.Commit ?? release.Installed.Commit;
        if (!SourceZipRules.ValidCommit(commit))
            throw new ProvisionRequestException(409, "source-commit-unknown", "The engine does not know which Construct commit to provision; name one in 'commit'.");
        if (!options.HostAdmin.Source.Enabled)
            throw new ProvisionRequestException(409, "unsupported-capability", "The source cache is disabled on this host.");

        StoredDesiredState state;
        if (request.Desired is { } given)
        {
            state = new StoredDesiredState(given, ProvisionScript.DesiredHash(given), clock.UtcNow, actor);
        }
        else
        {
            state = await desired.GetDesiredAsync(vm.Name, ct)
                ?? throw new ProvisionRequestException(409, "desired-state-missing", "No settings are stored for this VM yet; send 'desired' once.");
        }

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
            var entered = maintenance.TryEnter("mutation:" + JobKinds.Provision, id, vm.Name)
                ?? throw new ProvisionRequestException(503, "maintenance", "The host is in maintenance.");
            handle = new GateHandle(operation, entered);

            // Saved before the run: a retry, or a reprovision with no client, uses the same settings.
            if (request.Desired is not null) await desired.SetDesiredAsync(vm.Name, state, ct);
            var channel = HostUpdateTrust.Apply(await config.GetAsync<UpdatesConfig>("updates", ct) ?? HostAdminDefaults.Updates, options).Channel;
            var queued = new Job(id, JobKinds.Provision, vm.Name, vm.Owner, JobState.Queued, [], null, null, clock.UtcNow, null,
                actor, request.RequestId, ProvisionJob.Steps[0]);
            await jobs.UpsertAsync(queued, ct);
            var run = new ProvisionRun(id, vm, target, commit, state, channel, request.DeferProjectCommands,
                options.Iso.SeedUser, target.IsRoot ? targets.BootstrapPublicKey() : null, request.Secrets);
            await runner.StartPersistedAsync(queued, handle, (progress, token) => job.RunAsync(run, progress, token), CancellationToken.None);
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
