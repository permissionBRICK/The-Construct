using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>A user's update setting (12.1): reprovision my VMs after this host updated itself.</summary>
public sealed record UserUpdateSettings(bool AutoReprovision = false);

/// <summary>
/// VMs that asked to be reprovisioned once the named host update succeeded. <paramref name="Owner"/> is the user they
/// belonged to when they asked: a VM of that name owned by anybody else by then is not touched. <paramref name="Start"/>:
/// the followers a user asked to update by name, each started first when it is off; any other follower that is off is skipped.
/// </summary>
public sealed record UpdateFollowers(IReadOnlyList<string> Vms, string? Owner = null, IReadOnlyList<string>? Start = null);

/// <summary>The last host update whose VMs were reprovisioned.</summary>
public sealed record UpdateFollowed(string UpdateId);

/// <summary>
/// VMs follow their host's update (12.1, S28): each is reprovisioned with its stored settings by the
/// existing <c>provision</c> job, one VM after the other. Nothing here chooses whose VMs: callers pass
/// the caller's own VMs, or every VM for an admin, each with whether a user asked for it. A VM a user asked
/// for is started first when it is off (<see cref="ProvisionStart"/>); in a chain the engine runs by itself (the
/// follow-up of a host update, an admin's every-VM reprovision) a VM that is off is skipped before any job exists.
/// A VM that cannot start a job (no stored settings, another job running) is skipped and logged. A service
/// restart ends the sequence.
/// </summary>
public sealed class VmUpdates(IVmRepository vms, IUserStore users, IJobStore jobs, IHostConfigStore config, IServiceProvider services,
    ProvisionStart starter, IHostApplicationLifetime lifetime, ILogger<VmUpdates> logger)
{
    public static string SettingsKey(string user) => "user-updates:" + user.ToLowerInvariant();
    public static string FollowersKey(string updateId) => "update-follow:" + updateId;
    public const string FollowedKey = "update-followed";

    /// <summary>The primaries <paramref name="owner"/> owns on this host, or every primary when null.</summary>
    public async Task<IReadOnlyList<Vm>> PrimariesAsync(string? owner, CancellationToken ct) =>
        (await vms.ListAsync(owner, ct)).Where(v => v.Kind == VmKind.Primary && !v.Deleting && (owner is null || Ownership.SameName(v.Owner, owner)))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<UserUpdateSettings> SettingsAsync(string user, CancellationToken ct) =>
        await config.GetAsync<UserUpdateSettings>(SettingsKey(user), ct) ?? new();

    /// <summary>
    /// Starts the provision job of one VM with its stored settings and the engine's installed commit; <paramref name="device"/>
    /// is the device that asked for it (<see cref="JobDevices"/>), null when the engine starts it. <paramref name="startStopped"/>:
    /// a user asked for it, so a VM that is off is started first.
    /// </summary>
    public async Task<Job> SubmitAsync(Vm vm, string actor, string? device, bool startStopped, CancellationToken ct)
    {
        var (job, _) = await services.GetRequiredService<ProvisionJobs>().SubmitAsync(vm, actor,
            new ProvisionSubmission("update-" + Guid.NewGuid().ToString("n"), null, null, false, new JobSecretBag(), Device: device,
                StartStopped: startStopped), ct);
        return job;
    }

    /// <summary>
    /// Reprovisions the VMs one after the other in the background, each job for <paramref name="device"/>; answers their names.
    /// <c>StartStopped</c>: a user asked for that VM, so it is started first when it is off; otherwise a VM that is off is skipped.
    /// </summary>
    public IReadOnlyList<string> Start(IReadOnlyList<(Vm Vm, bool StartStopped)> list, string actor, string? device = null)
    {
        var queued = list.GroupBy(v => v.Vm.Name, StringComparer.OrdinalIgnoreCase).Select(g => (g.First().Vm, StartStopped: g.Any(v => v.StartStopped))).ToArray();
        var names = queued.Select(v => v.Vm.Name).ToArray();
        var stopping = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            foreach (var (name, owner, created, startStopped) in queued.Select(v => (v.Vm.Name, v.Vm.Owner, v.Vm.Created, v.StartStopped)))
            {
                if (stopping.IsCancellationRequested) break;
                try
                {
                    // The same VM as when it was queued: a VM deleted meanwhile and recreated under its name
                    // by somebody else is not touched.
                    if (await vms.GetAsync(name, stopping) is not { Deleting: false } vm ||
                        !Ownership.SameName(vm.Owner, owner) || vm.Created != created) continue;
                    // The engine's own chain never starts a VM: one that is off keeps its update due until someone starts it.
                    if (!startStopped && await starter.StoppedAsync(vm, stopping))
                    {
                        logger.LogInformation("Reprovision of {Vm} after an update skipped: the VM is off. Its update stays due.", name);
                        continue;
                    }

                    var job = await SubmitAsync(vm, actor, device, startStopped, stopping);
                    while (await jobs.GetAsync(job.Id, stopping) is { State: JobState.Queued or JobState.Running })
                        await Task.Delay(TimeSpan.FromSeconds(1), stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogInformation("Reprovision of {Vm} after an update skipped: {Reason}", name,
                        ex is ProvisionRequestException p ? p.Code : SafeError.Describe(ex));
                }
            }
        });
        return names;
    }

    /// <summary>
    /// After this host updated itself: the VMs of users with <c>updates.autoReprovision</c> and the VMs that
    /// asked for it (<see cref="FollowersKey"/>) are reprovisioned, once per update.
    /// </summary>
    public async Task<IReadOnlyList<string>> FollowAsync(HostUpdateRecord succeeded, CancellationToken ct)
    {
        if ((await config.GetAsync<UpdateFollowed>(FollowedKey, ct))?.UpdateId == succeeded.Id) return [];
        var list = new List<(Vm, bool)>();
        foreach (var user in await users.ListAsync(ct))
            if ((await SettingsAsync(user.Name, ct)).AutoReprovision) list.AddRange((await PrimariesAsync(user.Name, ct)).Select(v => (v, false)));
        var followers = await config.GetAsync<UpdateFollowers>(FollowersKey(succeeded.Id), ct);
        foreach (var name in followers?.Vms ?? [])
            if (await vms.GetAsync(name, ct) is { Kind: VmKind.Primary, Deleting: false } vm &&
                (followers!.Owner is null || Ownership.SameName(vm.Owner, followers.Owner)))
                list.Add((vm, followers.Start?.Any(s => Ownership.SameName(s, vm.Name)) == true));
        await config.SetAsync(FollowedKey, new UpdateFollowed(succeeded.Id), "system", ct);
        return list.Count == 0 ? [] : Start(list, "system");
    }
}
