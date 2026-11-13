using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>A user's update setting (12.1): reprovision my VMs after this host updated itself.</summary>
public sealed record UserUpdateSettings(bool AutoReprovision = false);

/// <summary>VMs that asked to be reprovisioned once the named host update succeeded.</summary>
public sealed record UpdateFollowers(IReadOnlyList<string> Vms);

/// <summary>The last host update whose VMs were reprovisioned.</summary>
public sealed record UpdateFollowed(string UpdateId);

/// <summary>
/// VMs follow their host's update (12.1, S28): each is reprovisioned with its stored settings by the
/// existing <c>provision</c> job, one VM after the other. Nothing here chooses whose VMs: callers pass
/// the caller's own VMs, or every VM for an admin. A VM that cannot start a job (stopped, no stored
/// settings, another job running) is skipped and logged. A service restart ends the sequence.
/// </summary>
public sealed class VmUpdates(IVmRepository vms, IUserStore users, IJobStore jobs, IHostConfigStore config, IServiceProvider services,
    ILogger<VmUpdates> logger)
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

    /// <summary>Starts the provision job of one VM with its stored settings and the engine's installed commit.</summary>
    public async Task<Job> SubmitAsync(Vm vm, string actor, CancellationToken ct)
    {
        var (job, _) = await services.GetRequiredService<ProvisionJobs>().SubmitAsync(vm, actor,
            new ProvisionSubmission("update-" + Guid.NewGuid().ToString("n"), null, null, false, new JobSecretBag()), ct);
        return job;
    }

    /// <summary>Reprovisions the VMs one after the other in the background; answers their names.</summary>
    public IReadOnlyList<string> Start(IReadOnlyList<Vm> list, string actor)
    {
        var queued = list.DistinctBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var names = queued.Select(v => v.Name).ToArray();
        _ = Task.Run(async () =>
        {
            foreach (var (name, owner, created) in queued.Select(v => (v.Name, v.Owner, v.Created)))
            {
                try
                {
                    // The same VM as when it was queued: a VM deleted meanwhile and recreated under its name
                    // by somebody else is not touched.
                    if (await vms.GetAsync(name, CancellationToken.None) is not { Deleting: false } vm ||
                        !Ownership.SameName(vm.Owner, owner) || vm.Created != created) continue;
                    var job = await SubmitAsync(vm, actor, CancellationToken.None);
                    while (await jobs.GetAsync(job.Id, CancellationToken.None) is { State: JobState.Queued or JobState.Running })
                        await Task.Delay(TimeSpan.FromSeconds(1));
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
        await config.SetAsync(FollowedKey, new UpdateFollowed(succeeded.Id), "system", ct);
        var list = new List<Vm>();
        foreach (var user in await users.ListAsync(ct))
            if ((await SettingsAsync(user.Name, ct)).AutoReprovision) list.AddRange(await PrimariesAsync(user.Name, ct));
        foreach (var name in (await config.GetAsync<UpdateFollowers>(FollowersKey(succeeded.Id), ct))?.Vms ?? [])
            if (await vms.GetAsync(name, ct) is { Kind: VmKind.Primary, Deleting: false } vm) list.Add(vm);
        return list.Count == 0 ? [] : Start(list, "system");
    }
}
