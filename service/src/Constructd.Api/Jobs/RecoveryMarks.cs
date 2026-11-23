using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Jobs;

/// <summary>
/// What a retried <c>reinstall</c> or <c>restore</c> needs to know about the attempts before it (design 7.1):
/// whether the VM was already deleted and which backup the reinstall relies on, and whether a restore
/// already started on the VM. Stored per VM incarnation in the host config store, so it survives an
/// engine restart. Public facts only: backup ids, a key fingerprint, times; never a key.
/// </summary>
public sealed class RecoveryMarks(IHostConfigStore config)
{
    /// <summary>
    /// A reinstall in progress. <paramref name="Fingerprint"/> is the job key's recipient on the VM;
    /// <paramref name="Nested"/> the setting the deleted VM had; <paramref name="DeletedAt"/> is set
    /// just before the delete, so from then on a retry starts at the create step.
    /// </summary>
    public sealed record Reinstall(DateTimeOffset Created, bool Active, string? JobId, string? BackupId, string? Fingerprint,
        bool Nested, DateTimeOffset? DeletedAt);

    /// <summary>A restore into the VM: set before the guest runs it, <paramref name="CompletedAt"/> when it finished.</summary>
    public sealed record Restore(DateTimeOffset Created, string BackupId, string JobId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);

    private static string ReinstallSection(Vm vm) => "reinstall:" + vm.Name.ToLowerInvariant();

    private static string RestoreSection(Vm vm) => "restore:" + vm.Name.ToLowerInvariant();

    public async Task<Reinstall?> GetReinstallAsync(Vm vm, CancellationToken ct)
    {
        var mark = await config.GetAsync<Reinstall>(ReinstallSection(vm), ct);
        return mark is { Active: true } && mark.Created == vm.Created ? mark : null;
    }

    public Task SetReinstallAsync(Vm vm, Reinstall mark, CancellationToken ct) =>
        config.SetAsync(ReinstallSection(vm), mark with { Created = vm.Created }, "system", ct);

    public Task ClearReinstallAsync(Vm vm, CancellationToken ct) =>
        config.SetAsync(ReinstallSection(vm), new Reinstall(vm.Created, false, null, null, null, false, null), "system", ct);

    public async Task<Restore?> GetRestoreAsync(Vm vm, CancellationToken ct)
    {
        var mark = await config.GetAsync<Restore>(RestoreSection(vm), ct);
        return mark?.Created == vm.Created ? mark : null;
    }

    public Task SetRestoreAsync(Vm vm, Restore? mark, CancellationToken ct) =>
        mark is null
            ? config.SetAsync(RestoreSection(vm), new Restore(default, "", "", default, null), "system", ct)
            : config.SetAsync(RestoreSection(vm), mark with { Created = vm.Created }, "system", ct);
}

/// <summary>
/// The private halves of reinstall job keys between a failed attempt and its retry, in this process's
/// memory only (design S29, 7.1). A reinstall that failed after the delete leaves its key here so a
/// retry can still restore the backup; it is dropped after <see cref="Lifetime"/>, on success, and
/// with the engine. Nothing here is ever written anywhere.
/// </summary>
public sealed class ReinstallKeys(IClock clock)
{
    /// <summary>How long a failed reinstall's key waits for a retry.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (string BackupId, string PrivatePem, DateTimeOffset Until)> _held = new(StringComparer.OrdinalIgnoreCase);

    public void Hold(string vm, string backupId, string privatePem) => _held[vm] = (backupId, privatePem, clock.UtcNow + Lifetime);

    /// <summary>The held key for this backup, or null when there is none (expired, other backup, engine restarted).</summary>
    public string? Take(string vm, string backupId)
    {
        if (!_held.TryRemove(vm, out var held)) return null;
        return held.BackupId == backupId && held.Until > clock.UtcNow ? held.PrivatePem : null;
    }

    public void Drop(string vm) => _held.TryRemove(vm, out _);
}
