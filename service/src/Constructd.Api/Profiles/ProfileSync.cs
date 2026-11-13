using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Api.Composition;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Sync.Profiles;
using Constructd.Sync.Replica;
using Constructd.Sync.Sealing;

namespace Constructd.Api.Profiles;

/// <summary>What a profile write through the engine came to (9.3).</summary>
public abstract record ProfileWriteOutcome
{
    /// <summary>
    /// The profile is at <paramref name="Revision"/> with <paramref name="File"/> (null: deleted), or,
    /// with <paramref name="Queued"/>, the store was not reached and the edit waits in the outbox;
    /// then <paramref name="Revision"/> is the base the writer keeps and <paramref name="File"/> its edit.
    /// </summary>
    public sealed record Written(long? Revision, byte[]? File, bool Queued) : ProfileWriteOutcome;

    public sealed record Conflict(string ConflictId, IReadOnlyList<string> Fields) : ProfileWriteOutcome;

    public sealed record Refused(int Status, string Code, string Message) : ProfileWriteOutcome;
}

/// <summary>What a resolve came to.</summary>
public abstract record ProfileResolveOutcome
{
    public sealed record Resolved(long Revision, byte[] File) : ProfileResolveOutcome;

    public sealed record AlreadyClosed : ProfileResolveOutcome;

    public sealed record Recomputed(string ConflictId) : ProfileResolveOutcome;

    public sealed record Refused(int Status, string Code, string Message) : ProfileResolveOutcome;
}

/// <summary>
/// Profile sync in the engine (docs/plans/construct-platform-design.md 9.3, 9.6; docs/config-sync.md,
/// "The new stack"). For every account a device granted this engine <c>D</c>, an
/// <see cref="EngineReplica"/> against the account's store (<see cref="EngineStores"/>). One loop
/// (<see cref="SyncAsync"/>, run by <see cref="ProfileSyncService"/> on a timer and when an edit is
/// queued) per account:
/// <list type="number">
/// <item>reads the projects folder of each VM whose heartbeat digest changed and queues its direct edits;</item>
/// <item>pushes the outbox and pulls the feed;</item>
/// <item>re-reads the <c>device</c> records, so a revocation reaches this engine (8.4);</item>
/// <item>writes changed profiles into the running VMs that hold or select them, guarded.</item>
/// </list>
/// VM and device writes go through <see cref="SubmitAsync"/>: queued with the writer's base, pushed at
/// once, answered with the result, a conflict, or "queued" when the store is not reachable.
/// </summary>
public sealed class ProfileSync : IDisposable
{
    /// <summary>How often the loop runs without being woken.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(60);

    private readonly EngineKeyStore _keys;
    private readonly IRecordKeyProvider _recordKeys;
    private readonly EngineStores _stores;
    private readonly EngineDevices _devices;
    private readonly IVmRepository _vms;
    private readonly IVmDesiredStateSource _desired;
    private readonly IProvisionTargets _targets;
    private readonly ISshRunner _ssh;
    private readonly ILogger<ProfileSync> _log;
    private readonly string _directory;
    private readonly bool _inMemory;

    private readonly ConcurrentDictionary<string, EngineReplica> _replicas = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _reads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loop = new(1, 1);
    private readonly SemaphoreSlim _folders = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _create = new();

    public ProfileSync(EngineKeyStore keys, IRecordKeyProvider recordKeys, EngineStores stores, EngineDevices devices, IVmRepository vms,
        IVmDesiredStateSource desired, IProvisionTargets targets, ISshRunner ssh, ConstructdOptions options, ILogger<ProfileSync> log)
    {
        _keys = keys;
        _recordKeys = recordKeys;
        _stores = stores;
        _devices = devices;
        _vms = vms;
        _desired = desired;
        _targets = targets;
        _ssh = ssh;
        _log = log;
        _inMemory = options.EffectivePersistence != PersistenceMode.Sqlite;
        _directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "sync");
        States = new VmProfileStates(_inMemory ? ":memory:" : Path.Combine(_directory, "vm-profiles.db"));
    }

    public VmProfileStates States { get; }

    public string EngineId => _keys.EngineId;

    /// <summary>The origin an edit of this VM carries (9.1 <c>origins</c>): unique across the account's engines.</summary>
    public string VmOrigin(string vm) => $"vm:{EngineId}/{vm}";

    public static string DeviceOrigin(string deviceId) => "device:" + deviceId;

    public void Dispose()
    {
        foreach (var replica in _replicas.Values) replica.Dispose();
        States.Dispose();
        _loop.Dispose();
        _folders.Dispose();
        _wake.Dispose();
    }

    // ---- the loop -------------------------------------------------------------------------------

    /// <summary>Wakes the loop: an edit was queued or a folder needs reading.</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Waits for <see cref="Wake"/> or the interval.</summary>
    public Task WaitAsync(CancellationToken ct) => _wake.WaitAsync(Interval, ct);

    /// <summary>The replica of an account this engine takes part in; null when no device granted it <c>D</c>.</summary>
    public EngineReplica? Replica(string account)
    {
        if (_replicas.TryGetValue(account, out var existing)) return existing;
        if (!_keys.Accounts(EngineGrants.Records).Contains(account, StringComparer.Ordinal)) return null;
        lock (_create)
        {
            if (_replicas.TryGetValue(account, out existing)) return existing;
            var path = _inMemory ? ":memory:" : Path.Combine(_directory, "replica-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..16] + ".db");
            var replica = new EngineReplica(path, account, EngineId, _stores.For(account), _recordKeys);
            _replicas[account] = replica;
            return replica;
        }
    }

    /// <summary>One pass of the loop over every account. Passes never overlap.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        await _loop.WaitAsync(ct);
        try
        {
            foreach (var account in _keys.Accounts(EngineGrants.Records))
            {
                try
                {
                    await SyncAccountAsync(account, ct);
                }
                catch (Exception ex) when (ex is StoreUnavailableException or CryptographicException or HttpRequestException)
                {
                    _log.LogWarning("Profile sync of account {Account} did not complete: {Reason}", account, ex.Message);
                }
            }
        }
        finally { _loop.Release(); }
    }

    private async Task SyncAccountAsync(string account, CancellationToken ct)
    {
        var replica = Replica(account)!;
        var vms = (await _vms.ListAsync(account, ct)).Where(v => v.Owner == account && v.State == VmState.Running && !v.Deleting).ToList();

        foreach (var vm in vms)
        {
            if (_reads.TryRemove(vm.Name, out var digest)) await ReadFolderAsync(vm, replica, digest, ct);
        }

        try
        {
            var pushed = await replica.PushAsync(ct);
            foreach (var reason in pushed.Failed) _log.LogWarning("A profile edit was refused by the store and stays queued: {Reason}", reason);
            while (await replica.PullAsync(default, ct) > 0) { }
        }
        catch (Exception ex) when (ex is StoreUnavailableException or HttpRequestException)
        {
            _log.LogInformation("The store of account {Account} is not reachable; edits stay queued: {Reason}", account, ex.Message);
        }

        // A revocation reaches this engine with the device records (8.4).
        try
        {
            await _devices.RefreshAsync(account, ct);
        }
        catch (Exception ex) when (ex is StoreUnavailableException or HttpRequestException)
        {
            _log.LogInformation("The device records of account {Account} could not be read: {Reason}", account, ex.Message);
        }

        // From the replica, store reachable or not: restores and what arrived earlier.
        foreach (var vm in vms) await WriteBackAsync(vm, replica, ct);
    }

    // ---- writes from VMs and devices (9.3) ------------------------------------------------------

    /// <summary>
    /// A VM or device writes (<paramref name="profile"/>) or deletes (null) a profile on
    /// <paramref name="baseRevision"/>, the revision it last saw (null: it has none). The engine finds
    /// that revision's content, queues the edit with it, pushes at once and answers with the result.
    /// A base the engine no longer knows counts as none: the store decides, and a profile that exists
    /// becomes a conflict with no base instead of being overwritten.
    /// </summary>
    public async Task<ProfileWriteOutcome> SubmitAsync(string account, string name, JsonObject? profile, long? baseRevision, string origin, Vm? vm, CancellationToken ct)
    {
        if (!VmProfileFolder.SafeName(name)) return new ProfileWriteOutcome.Refused(400, "invalid-name", "A profile name is one path component.");
        if (Replica(account) is not { } replica)
            return new ProfileWriteOutcome.Refused(409, "not-participating", "No device has granted this engine the record key of this account yet.");

        var id = EngineReplica.ProfileId(name);
        var (found, baseValue) = baseRevision is > 0 ? await replica.ValueAtAsync(id, baseRevision.Value, ct) : (false, null);
        long? seq;
        try
        {
            seq = await replica.SubmitAsync(name, profile, found ? baseRevision!.Value : 0, found ? baseValue : null, origin, ct);
        }
        catch (InvalidProfileException ex)
        {
            return new ProfileWriteOutcome.Refused(400, "invalid-profile", ex.Message);
        }
        catch (PayloadTooNewException ex)
        {
            return new ProfileWriteOutcome.Refused(409, "payload-too-new", ex.Message);
        }

        SubmitOutcome outcome = new SubmitOutcome.Queued();
        if (seq is { } entry)
        {
            try
            {
                await replica.PushAsync(ct);
                while (await replica.PullAsync(default, ct) > 0) { }
            }
            catch (Exception ex) when (ex is StoreUnavailableException or HttpRequestException)
            {
                _log.LogInformation("The store is not reachable; a profile edit of {Origin} stays queued: {Reason}", origin, ex.Message);
            }

            outcome = replica.TakeOutcome(entry);
        }
        else
        {
            outcome = new SubmitOutcome.Written((await replica.GetAsync(id, ct))?.Revision ?? 0);
        }

        ProfileWriteOutcome result = outcome switch
        {
            SubmitOutcome.Written written => await WrittenAsync(replica, id, written.Revision, ct),
            SubmitOutcome.Conflict conflict => new ProfileWriteOutcome.Conflict(conflict.ConflictId,
                (await replica.ConflictsAsync(ct)).FirstOrDefault(c => c.Id == conflict.ConflictId)?.Fields ?? []),
            SubmitOutcome.Failed failed => new ProfileWriteOutcome.Refused(409, "refused-by-store", failed.Reason),
            // Queued: the writer keeps its .rev and writes its edit as the engine will send it.
            _ => new ProfileWriteOutcome.Written(baseRevision, profile is null ? null : VmProfileFolder.Format(ProfileRules.Finish(ProfileRules.Normalize(profile))), true),
        };

        // The VM writes what the answer says; the engine notes that as the folder's state.
        if (vm is not null && result is ProfileWriteOutcome.Written w)
        {
            States.Set(vm.Name, new VmProfileState(name, w.Revision, w.File is null ? null : Encoding.UTF8.GetString(w.File), VmProfileFolder.Fingerprint(w.Revision, w.File, false)));
        }

        Wake();
        return result;
    }

    private static async Task<ProfileWriteOutcome> WrittenAsync(EngineReplica replica, string id, long revision, CancellationToken ct)
    {
        if (revision <= 0) return new ProfileWriteOutcome.Written(null, null, false);
        var (found, value) = await replica.ValueAtAsync(id, revision, ct);
        if (!found && await replica.GetAsync(id, ct) is { } current)
        {
            revision = current.Revision;
            value = current.Value;
        }

        return new ProfileWriteOutcome.Written(revision, value is null ? null : VmProfileFolder.Format(value), false);
    }

    /// <summary>Resolves a conflict (9.6): the profile and the closing of the conflict in one batch.</summary>
    public async Task<ProfileResolveOutcome> ResolveAsync(string account, string conflictId, JsonObject profile, string origin, Vm? vm, CancellationToken ct)
    {
        if (Replica(account) is not { } replica)
            return new ProfileResolveOutcome.Refused(409, "not-participating", "No device has granted this engine the record key of this account yet.");
        ResolveOutcome outcome;
        try
        {
            outcome = await replica.ResolveAsync(conflictId, profile, origin, ct);
        }
        catch (InvalidProfileException ex)
        {
            return new ProfileResolveOutcome.Refused(400, "invalid-profile", ex.Message);
        }
        catch (Exception ex) when (ex is StoreUnavailableException or HttpRequestException)
        {
            return new ProfileResolveOutcome.Refused(503, "store-unavailable", "The store is not reachable; resolve again later.");
        }

        Wake();
        switch (outcome)
        {
            case ResolveOutcome.Resolved resolved:
                var name = ProfileRules.Text(profile["name"]);
                var written = (ProfileWriteOutcome.Written)await WrittenAsync(replica, EngineReplica.ProfileId(name), resolved.ProfileRevision, ct);
                if (vm is not null)
                    States.Set(vm.Name, new VmProfileState(name, written.Revision, written.File is null ? null : Encoding.UTF8.GetString(written.File), VmProfileFolder.Fingerprint(written.Revision, written.File, false)));
                return new ProfileResolveOutcome.Resolved(written.Revision ?? resolved.ProfileRevision, written.File ?? []);
            case ResolveOutcome.AlreadyClosed:
                return new ProfileResolveOutcome.AlreadyClosed();
            case ResolveOutcome.Recomputed recomputed:
                return new ProfileResolveOutcome.Recomputed(recomputed.ConflictId);
            case ResolveOutcome.Refused refused:
                return new ProfileResolveOutcome.Refused(409, refused.Code, refused.Message);
            default:
                throw new InvalidOperationException();
        }
    }

    // ---- heartbeat ------------------------------------------------------------------------------

    /// <summary>
    /// A VM's heartbeat: a changed digest schedules a read of its folder; the answer is the open
    /// conflicts that VM's edits are part of (9.6), for its agent to resolve.
    /// </summary>
    public async Task<IReadOnlyList<ConflictView>> HeartbeatAsync(Vm vm, string? digest, CancellationToken ct)
    {
        if (Replica(vm.Owner) is not { } replica) return [];
        if (digest is { Length: > 0 and <= 128 } && digest != States.Digest(vm.Name))
        {
            _reads[vm.Name] = digest;
            Wake();
        }

        return await ConflictsForAsync(replica, vm, ct);
    }

    public async Task<IReadOnlyList<ConflictView>> ConflictsForAsync(EngineReplica replica, Vm vm, CancellationToken ct)
    {
        var origin = VmOrigin(vm.Name);
        return (await replica.ConflictsAsync(ct)).Where(c => Origins(c.Value).Contains(origin, StringComparer.Ordinal)).ToList();
    }

    private static IEnumerable<string> Origins(JsonObject conflict) =>
        new[] { "ours", "theirs" }.SelectMany(side => conflict["identities"]?[side]?["origins"] as JsonArray ?? [])
            .Select(o => o is JsonValue v && v.TryGetValue<string>(out var s) ? s : "");

    /// <summary>Schedules a read of a VM's folder at the next pass, whatever its digest.</summary>
    public void ScheduleRead(string vm)
    {
        _reads[vm] = null;
        Wake();
    }

    // ---- reading a VM's folder (direct edits) ---------------------------------------------------

    /// <summary>
    /// Reads the VM's projects folder over SSH (port of <c>Read-ConstructVmStore</c>) and compares
    /// each name with what the engine last saw there. A changed valid file is an edit on the
    /// revision in its <c>.rev</c>; a deletion marker is a deletion; a missing file is noted, so the
    /// write-back restores it (only <c>construct project delete</c> deletes). An invalid file is
    /// skipped. The digest is recorded only after a complete read.
    /// </summary>
    public async Task ReadFolderAsync(Vm vm, EngineReplica replica, string? digest, CancellationToken ct)
    {
        await _folders.WaitAsync(ct);
        try
        {
            if (await RunAsync(vm, VmProfileFolder.ReadScript, ct) is not { } lines || VmProfileFolder.ParseRead(lines) is not { } read)
            {
                _log.LogInformation("The projects folder of {Vm} could not be read; it is read again at its next heartbeat.", vm.Name);
                return;
            }

            var states = States.Get(vm.Name);
            var origin = VmOrigin(vm.Name);
            if (read.StoreExists && !read.Entries.Any(e => e.File is not null || e.Marker) && states.Values.Any(s => s.Content is not null))
            {
                // Mass-deletion guard (docs/config-sync.md 6): nothing is deleted here anyway; the files come back.
                _log.LogWarning("The projects folder of {Vm} holds no profiles; restoring them.", vm.Name);
            }

            foreach (var entry in read.Entries)
            {
                if (states.TryGetValue(entry.Name, out var known) && known.Fingerprint == entry.Fingerprint) continue;
                var id = EngineReplica.ProfileId(entry.Name);
                var (found, baseValue) = entry.Revision is > 0 ? await replica.ValueAtAsync(id, entry.Revision.Value, ct) : (false, null);

                if (entry.File is { } file)
                {
                    if (Parse(file) is not { } profile || ProfileRules.Validate(profile).Count > 0 || ProfileRules.Text(profile["name"]) != entry.Name)
                    {
                        _log.LogWarning("Profile {Profile} in the folder of {Vm} is not valid; it is skipped until it is.", entry.Name, vm.Name);
                        continue;
                    }

                    var current = await replica.GetAsync(id, ct);
                    var clean = found ? ProfileRules.Equivalent(profile, baseValue)
                        : entry.Revision is null && current is { Deleted: false } && ProfileRules.Equivalent(profile, current.Value);
                    if (!clean) await QueueAsync(replica, entry.Name, profile, found ? entry.Revision!.Value : 0, found ? baseValue : null, origin, vm, ct);
                    States.Set(vm.Name, new VmProfileState(entry.Name, entry.Revision, Encoding.UTF8.GetString(file), entry.Fingerprint));
                }
                else if (entry.Marker)
                {
                    await QueueAsync(replica, entry.Name, null, found ? entry.Revision!.Value : 0, found ? baseValue : null, origin, vm, ct);
                    States.Set(vm.Name, new VmProfileState(entry.Name, entry.Revision, null, entry.Fingerprint));
                }
                else
                {
                    // Only a .rev left: the file is missing and is restored by the write-back.
                    States.Set(vm.Name, new VmProfileState(entry.Name, entry.Revision, null, entry.Fingerprint));
                }
            }

            // Names the engine knew in this folder that are gone altogether (file, .rev and marker).
            var present = read.Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in states.Values.Where(s => !present.Contains(s.Profile)))
                States.Set(vm.Name, new VmProfileState(gone.Profile, null, null, VmProfileFolder.Fingerprint(null, null, false)));

            if (digest is not null) States.SetDigest(vm.Name, digest);
        }
        finally { _folders.Release(); }
    }

    private async Task QueueAsync(EngineReplica replica, string name, JsonObject? profile, long baseRevision, JsonNode? baseValue, string origin, Vm vm, CancellationToken ct)
    {
        try
        {
            if (await replica.SubmitAsync(name, profile, baseRevision, baseValue, origin, ct) is { } seq) replica.TakeOutcome(seq);
        }
        catch (Exception ex) when (ex is InvalidProfileException or PayloadTooNewException)
        {
            _log.LogWarning("A direct edit of {Profile} in {Vm} was not taken: {Reason}", name, vm.Name, ex.Message);
        }
    }

    // ---- writing back into VMs (9.3, docs/config-sync.md 6) -------------------------------------

    /// <summary>
    /// Writes each profile the VM holds or selects whose store revision it does not have yet, and
    /// restores missing files. Guarded: a name whose folder state is not what the engine last saw is
    /// left alone and the folder is read again, so a VM edit is merged, never overwritten. Names
    /// with an edit still queued, or with an open conflict this VM is part of, wait.
    /// </summary>
    public async Task WriteBackAsync(Vm vm, EngineReplica replica, CancellationToken ct)
    {
        await _folders.WaitAsync(ct);
        try
        {
            var states = States.Get(vm.Name);
            var selected = (await _desired.GetDesiredAsync(vm.Name, ct))?.Desired.Projects ?? [];
            var names = states.Keys.Concat(selected).Where(VmProfileFolder.SafeName).Distinct(StringComparer.Ordinal).ToList();
            var conflicted = (await ConflictsForAsync(replica, vm, ct)).Select(c => c.ProfileId).ToHashSet(StringComparer.Ordinal);

            var writes = new List<(VmProfileWrite Write, string? Content)>();
            foreach (var name in names)
            {
                var id = EngineReplica.ProfileId(name);
                if (await replica.GetAsync(id, ct) is not { } current) continue;
                states.TryGetValue(name, out var state);
                if (state?.Revision > current.Revision || conflicted.Contains(id) || await replica.HasPendingAsync(id, ct)) continue;

                // A deleted profile the VM does not have is nothing to write.
                if (current.Deleted && state is null) continue;
                var file = current.Deleted ? null : VmProfileFolder.Format(current.Value!);
                var wanted = VmProfileFolder.Fingerprint(current.Revision, file, false);
                if (state?.Fingerprint == wanted) continue;
                writes.Add((new VmProfileWrite(name, state?.Fingerprint ?? VmProfileFolder.Fingerprint(null, null, false), current.Revision, file),
                    file is null ? null : Encoding.UTF8.GetString(file)));
            }

            if (writes.Count == 0) return;
            if (await RunAsync(vm, VmProfileFolder.WriteScript(writes.Select(w => w.Write)), ct) is not { } lines
                || VmProfileFolder.ParseWrite(lines) is not { } result)
            {
                _log.LogInformation("Profiles could not be written into {Vm}; trying again at the next pass.", vm.Name);
                return;
            }

            foreach (var (write, content) in writes)
            {
                if (result.Done.TryGetValue(write.Name, out var fingerprint))
                    States.Set(vm.Name, new VmProfileState(write.Name, write.Revision, content, fingerprint));
            }

            if (result.Skipped.Count > 0)
            {
                // Somebody changed those files since the engine last looked: read them before writing again.
                _reads[vm.Name] = null;
            }
        }
        finally { _folders.Release(); }
    }

    // ---- internals ------------------------------------------------------------------------------

    private async Task<List<string>?> RunAsync(Vm vm, string script, CancellationToken ct)
    {
        if (await _targets.ResolveAsync(vm, ct) is not { } target) return null;
        var lines = new List<string>();
        var collector = new LineCollector(lines);
        int exit;
        try
        {
            exit = await _ssh.RunAsync(target, target.IsRoot ? "bash -s" : "sudo -n bash -s", script, collector, SshTimeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation("SSH to {Vm} failed: {Reason}", vm.Name, ex.Message);
            return null;
        }

        return exit == 0 ? lines : null;
    }

    private static JsonObject? Parse(byte[] file)
    {
        try { return JsonNode.Parse(file) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Collects lines synchronously (<see cref="Progress{T}"/> would post them later).</summary>
    private sealed class LineCollector(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines) lines.Add(value);
        }
    }
}
