using System.Text.Json.Nodes;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Profiles;
using Constructd.Sync.Sealing;

namespace Constructd.Sync.Replica;

/// <summary>
/// A new version of one file an upstream config repo tracks for a profile (9.7), as the device that fetched it
/// submits it: <paramref name="OperationId"/> names the submission (a retry carries the same), <paramref name="Url"/>
/// and <paramref name="Path"/> the file's provenance, <paramref name="Commit"/> the upstream commit and
/// <paramref name="Content"/> the file at that commit.
/// </summary>
public sealed record UpstreamVersion(string OperationId, string Url, string Path, string? Ref, string Commit, JsonObject Content, bool Import = false);

/// <summary>The base a device knows from before the engine stored one: the old client's manifest and stored base (13.1).</summary>
public sealed record UpstreamBase(string? Commit, JsonObject Content);

/// <summary>What an upstream version came to.</summary>
public abstract record UpstreamOutcome
{
    /// <summary>Nothing new: this operation or commit was applied already, or the file is what the base holds.</summary>
    public sealed record Unchanged : UpstreamOutcome;

    /// <summary>Merged: the profile is at <paramref name="ProfileRevision"/> (unchanged when only the base moved), the base is the new version.</summary>
    public sealed record Applied(long ProfileRevision) : UpstreamOutcome;

    /// <summary>Both sides changed the same fields: a conflict record holds the version, and the base moved with it.</summary>
    public sealed record Conflict(string ConflictId, IReadOnlyList<string> Fields) : UpstreamOutcome;

    /// <summary>An open conflict on the profile is resolved first; nothing was written.</summary>
    public sealed record Waiting(string ConflictId) : UpstreamOutcome;

    /// <summary>The profile was deleted here: the file is no longer tracked and its base is gone (config-sync 7).</summary>
    public sealed record Dropped : UpstreamOutcome;

    public sealed record Refused(string Code, string Message) : UpstreamOutcome;
}

public sealed partial class EngineReplica
{
    /// <summary>The <c>upstream-applied</c> record of a profile: one per imported file (the profile namespace is flat).</summary>
    public static string UpstreamAppliedId(string name) => "upstream-applied:" + name;

    /// <summary>
    /// Merges a new upstream version of a tracked file into profile <paramref name="name"/> (9.7) with the 9.6 rules:
    /// base = the stored base (or <paramref name="knownBase"/> when the engine has none yet), ours = the upstream
    /// version, theirs = the profile here, so a field both changed keeps the local value and becomes a conflict. The
    /// profile, or the conflict, is written together with the new base in one batch; a retry of the same operation, or
    /// of a commit applied already, changes nothing. A profile of that name that came from elsewhere is never
    /// overwritten; one deleted here is no longer tracked. Online: the store must be reachable.
    /// </summary>
    /// <param name="origin">Who submitted it (<c>device:&lt;id&gt;</c>), the base's provenance.</param>
    /// <param name="deliverTo">Origins of the VMs that use the profile: a conflict reaches their agents (9.6).</param>
    public async Task<UpstreamOutcome> ApplyUpstreamAsync(string name, UpstreamVersion version, UpstreamBase? knownBase, string origin,
        IReadOnlyCollection<string> deliverTo, CancellationToken ct)
    {
        var incoming = version.Content.DeepClone().AsObject();
        incoming["name"] = name;
        var problems = ProfileRules.Validate(incoming);
        if (problems.Count > 0) return new UpstreamOutcome.Refused("invalid-profile", "The upstream file is not a valid profile: " + string.Join("; ", problems.Select(p => p.Message)));
        incoming = ProfileRules.Finish(ProfileRules.Normalize(incoming));

        var profileId = ProfileId(name);
        var appliedId = UpstreamAppliedId(name);
        await _lock.WaitAsync(ct);
        try
        {
            var epoch = await _store.ActiveEpochAsync(ct);
            var key = await _keys.GetKeyAsync(_account, epoch, ct);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var appliedRecord = await _store.GetAsync(appliedId, ct);
                var appliedPayload = appliedRecord is null ? null : await OpenAsync(appliedRecord, ct);
                var applied = RecordPayload.ValueOf(appliedPayload) as JsonObject;
                var profile = await _store.GetAsync(profileId, ct);

                // Deleted here (config-sync 7), now or since the base was written, also when the name was created again
                // (the store keeps a record's last deletion): the base goes and upstream never brings the profile back.
                // An old client's base predates every deletion in the store. An explicit user import (version.Import)
                // starts fresh on a deleted profile instead of dropping it, while still refusing to overwrite an
                // existing profile created locally after the deletion.
                var deletedSinceBase = applied is not null && profile?.DeletedRevision > appliedRecord!.Revision;
                if (version.Import && (profile is { Deleted: true } || deletedSinceBase))
                {
                    applied = null;
                }
                else if (profile is { Deleted: true }
                    || deletedSinceBase
                    || applied is null && knownBase is not null && profile?.DeletedRevision is not null)
                {
                    if (appliedRecord is not { Deleted: false }) return new UpstreamOutcome.Dropped();
                    var drop = Sealed(key, appliedId, RecordKinds.UpstreamApplied, epoch, appliedRecord.Revision, true, RecordPayload.Rewrite(null, null, [origin], _supported));
                    switch (await _store.BatchAsync([drop.Write], ct))
                    {
                        case BatchOutcome.Accepted dropped:
                            Upsert(null, dropped.Records[0], null);
                            return new UpstreamOutcome.Dropped();
                        case BatchOutcome.Refused refused:
                            return new UpstreamOutcome.Refused(refused.Code, refused.Message);
                        default:
                            continue;
                    }
                }

                if (applied is not null)
                {
                    if (ProfileRules.Text(applied["url"]) != version.Url || ProfileRules.Text(applied["path"]) != version.Path)
                        return new UpstreamOutcome.Refused("other-provenance", $"Profile '{name}' is tracked from another file ({ProfileRules.Text(applied["path"])} of another repository or path).");
                    // A base the device brings counts only where the engine has none: another device's newer one stays.
                    if (knownBase is not null || ProfileRules.Text(applied["operationId"]) == version.OperationId
                        || version.Commit.Length > 0 && ProfileRules.Text(applied["baseCommit"]) == version.Commit)
                        return new UpstreamOutcome.Unchanged();
                    // The conflict the base's version came to, read from the store: another engine's replica may not
                    // have it yet. One written meanwhile moves the base, so the batch below is stale and this runs again.
                    if (ProfileRules.Text(applied["conflict"]) is { Length: > 0 } open && await _store.GetAsync(open, ct) is { Deleted: false })
                        return new UpstreamOutcome.Waiting(open);
                }

                var profilePayload = profile is null ? null : await OpenAsync(profile, ct);
                if (OpenConflictOn(profileId) is { } waiting) return new UpstreamOutcome.Waiting(waiting);

                var local = RecordPayload.ValueOf(profilePayload) as JsonObject;
                if (RecordPayload.VersionOf(profilePayload) > _supported)
                    return new UpstreamOutcome.Refused("payload-too-new", new PayloadTooNewException(RecordPayload.VersionOf(profilePayload), _supported).Message);
                var stored = applied?["baseContent"] as JsonObject ?? knownBase?.Content;
                JsonObject? @base = null;
                if (stored is not null)
                {
                    @base = stored.DeepClone().AsObject();
                    @base["name"] = name;
                }

                if (applied is null && local is not null && @base is null)
                    return new UpstreamOutcome.Refused("not-tracked", $"A profile named '{name}' exists that did not come from this file; it is not overwritten.");
                if (applied is not null && @base is not null && ProfileRules.Equivalent(incoming, @base)) return new UpstreamOutcome.Unchanged();

                var writes = new List<(RecordWrite Write, JsonObject? Payload)>();
                UpstreamOutcome done;
                if (local is null)
                {
                    // A tracked file new here: imported as it is.
                    var write = Sealed(key, profileId, RecordKinds.Profile, epoch, profile?.Revision ?? 0, false,
                        RecordPayload.Rewrite(profilePayload, incoming, RecordPayload.OriginsOf(profilePayload).Append(origin), _supported));
                    writes.Add(write);
                    done = new UpstreamOutcome.Applied(0);
                }
                else
                {
                    var merge = ProfileMerge.Merge(@base, incoming, local);
                    if (merge.Clean)
                    {
                        if (!ProfileMerge.Same(merge.Merged, local))
                            writes.Add(Sealed(key, profileId, RecordKinds.Profile, epoch, profile!.Revision, false,
                                RecordPayload.Rewrite(profilePayload, merge.Merged, RecordPayload.OriginsOf(profilePayload).Append(origin), _supported)));
                        done = new UpstreamOutcome.Applied(profile!.Revision);
                    }
                    else
                    {
                        var oursOrigins = deliverTo.Prepend(origin).ToList();
                        var theirsOrigins = RecordPayload.OriginsOf(profilePayload);
                        var conflictId = ConflictId(profileId, @base, incoming, local);
                        // The same conflict computed before: open, it is there already; closed, it was resolved, and only the base moves.
                        if (await _store.GetAsync(conflictId, ct) is null)
                        {
                            var value = ConflictValue(profileId, profile!.Revision, @base, incoming, local, merge.Merged, merge.Conflicts,
                                Actor, oursOrigins, profile.Actor, theirsOrigins);
                            writes.Add(Sealed(key, conflictId, RecordKinds.Conflict, epoch, 0, false,
                                RecordPayload.Rewrite(null, value, oursOrigins.Concat(theirsOrigins), _supported)));
                        }

                        done = new UpstreamOutcome.Conflict(conflictId, merge.Conflicts);
                    }
                }

                var appliedValue = new JsonObject
                {
                    ["profile"] = name, ["url"] = version.Url, ["path"] = version.Path, ["ref"] = version.Ref,
                    ["baseCommit"] = version.Commit, ["baseContent"] = incoming.DeepClone(), ["operationId"] = version.OperationId, ["by"] = origin,
                };
                if (done is UpstreamOutcome.Conflict opened) appliedValue["conflict"] = opened.ConflictId;
                writes.Add(Sealed(key, appliedId, RecordKinds.UpstreamApplied, epoch, appliedRecord?.Revision ?? 0, false,
                    RecordPayload.Rewrite(appliedRecord is { Deleted: false } ? appliedPayload : null, appliedValue, [origin], _supported)));

                switch (await _store.BatchAsync(writes.Select(w => w.Write).ToList(), ct))
                {
                    case BatchOutcome.Accepted accepted:
                        using (var tx = _db.BeginTransaction())
                        {
                            for (var i = 0; i < writes.Count; i++) Upsert(tx, accepted.Records[i], writes[i].Payload);
                            tx.Commit();
                        }

                        var written = accepted.Records.FirstOrDefault(r => r.Id == profileId);
                        return done is UpstreamOutcome.Applied a ? new UpstreamOutcome.Applied(written?.Revision ?? a.ProfileRevision) : done;
                    case BatchOutcome.Refused refused:
                        return new UpstreamOutcome.Refused(refused.Code, refused.Message);
                }

                // Stale: somebody wrote the profile or the base meanwhile; read both again.
            }

            throw new StoreUnavailableException("The profile kept changing while the upstream version was being applied.");
        }
        finally { _lock.Release(); }
    }

    /// <summary>The open conflict this replica knows on a profile, if any.</summary>
    private string? OpenConflictOn(string profileId) =>
        Replicas($"SELECT {ReplicaColumns} FROM replica WHERE account = $a AND kind = 'conflict' AND deleted = 0 ORDER BY revision", ("$a", _account))
            .Where(r => r.Value is JsonObject v && ProfileRules.Text(v["profile"]) == profileId)
            .Select(r => r.Id).FirstOrDefault();
}
