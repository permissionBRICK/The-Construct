using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>A file waiting for the user's keep/redact/delete, as <c>GET /vault/files</c> shows it.</summary>
public sealed record VaultFileView(string Id, string Vm, string Path, IReadOnlyList<string> Names, string Type, long Size, long CreatedAt);

public sealed partial class VaultHostService
{
    public const int MaxHits = 10000;
    private static readonly string[] SqliteSides = ["-wal", "-shm", "-journal"];

    // ── scheduling ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The periodic pass: closes expired approvals, expires leases, turns due cleanups into scan jobs,
    /// prunes old tombstones and forgets the vault records of VMs that no longer exist.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        lock (pendingGate) ExpireLocked(clock.UtcNow);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;
            foreach (var owner in await store.ListOwnersAsync(ct).ConfigureAwait(false))
            {
                var expired = (await store.ListAsync<VaultLease>(owner.Owner, ct).ConfigureAwait(false)).Where(l => l.ExpiresAt <= now).ToArray();
                if (expired.Length > 0)
                {
                    var entries = await store.ListEntriesAsync(owner.Owner, ct).ConfigureAwait(false);
                    foreach (var lease in expired)
                    {
                        await EndAsync(lease, now, entries, ct).ConfigureAwait(false);
                        await RecordAsync(owner.Owner, lease.Vm, $"Access to {lease.Name} expired; scrubbing the VM.", false, ct).ConfigureAwait(false);
                    }
                }
                await ScheduleAsync(owner.Owner, now, ct).ConfigureAwait(false);
            }
            await store.PruneTombstonesAsync((now - VaultRules.TombstoneAge).ToUnixTimeMilliseconds(), ct).ConfigureAwait(false);
            await PurgeOrphansAsync(ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The deletion fence: a VM whose removal was accepted keeps no leases, pending scrubs, file decisions,
    /// approvals or unlocked key. Its disk goes away, and a new VM of the same name starts clean.
    /// </summary>
    public async Task PurgeVmAsync(string vmName, CancellationToken ct)
    {
        Drop(vmName);
        CancelPending(vmName, "The VM is being deleted.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await PurgeAsync(r => Ownership.SameName(r.Vm, vmName), ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task PurgeOrphansAsync(CancellationToken ct)
    {
        var known = (await vms.ListAsync(null, ct).ConfigureAwait(false)).Where(v => !v.Deleting).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await PurgeAsync(r => !known.Contains(r.Vm), ct).ConfigureAwait(false);
        foreach (var name in unlocked.Keys.Where(n => !known.Contains(n)).ToArray()) Drop(name);
    }

    private async Task PurgeAsync(Func<IVaultRecord, bool> match, CancellationToken ct)
    {
        foreach (var record in (await store.ListAsync<VaultLease>(null, ct).ConfigureAwait(false)).Where(match)) await store.DeleteAsync<VaultLease>(record.Id, ct).ConfigureAwait(false);
        foreach (var record in (await store.ListAsync<VaultCleanup>(null, ct).ConfigureAwait(false)).Where(match)) await store.DeleteAsync<VaultCleanup>(record.Id, ct).ConfigureAwait(false);
        foreach (var record in (await store.ListAsync<VaultScrubJob>(null, ct).ConfigureAwait(false)).Where(match)) await store.DeleteAsync<VaultScrubJob>(record.Id, ct).ConfigureAwait(false);
        foreach (var record in (await store.ListAsync<VaultFileDecision>(null, ct).ConfigureAwait(false)).Where(match)) await store.DeleteAsync<VaultFileDecision>(record.Id, ct).ConfigureAwait(false);
    }

    // One scan per VM at a time, for every cleanup that is due — unless that VM holds the same value
    // again: that lease's own end scrubs it.
    private async Task ScheduleAsync(string owner, DateTimeOffset now, CancellationToken ct)
    {
        var cleanups = await store.ListAsync<VaultCleanup>(owner, ct).ConfigureAwait(false);
        if (!cleanups.Any(c => c.DueAt <= now)) return;
        var leases = await store.ListAsync<VaultLease>(owner, ct).ConfigureAwait(false);
        var entries = await store.ListEntriesAsync(owner, ct).ConfigureAwait(false);
        var jobs = await store.ListAsync<VaultScrubJob>(owner, ct).ConfigureAwait(false);
        foreach (var group in cleanups.Where(c => c.DueAt <= now).GroupBy(c => c.Vm, StringComparer.OrdinalIgnoreCase))
        {
            if (jobs.Any(j => j.Step == "scan" && Ownership.SameName(j.Vm, group.Key))) continue;
            var due = group.Where(c => !(ActiveLease(leases, c.Vm, c.Copy.Name, now) is not null && Live(entries, c.Copy.Name)?.Payload == c.Copy.Payload)).ToArray();
            if (due.Length == 0) continue;
            var items = due.Select(c => c.Copy).DistinctBy(c => (c.Name, c.Payload)).ToArray();
            await store.SaveAsync(new VaultScrubJob(NewId(), owner, due[0].Vm, "scan", "scan", items, [], null, now, now, null, []), ct).ConfigureAwait(false);
            foreach (var cleanup in due) await store.DeleteAsync<VaultCleanup>(cleanup.Id, ct).ConfigureAwait(false);
        }
    }

    private static bool Due(VaultScrubJob job, DateTimeOffset now) =>
        job.NotBefore <= now && (job.DeliveredAt is not { } delivered || delivered + RedeliverAfter <= now);
    private static bool NeedsKey(VaultScrubJob job) => job.Step == "scan" || job.Files.Any(f => f.Action == "redact");

    /// <summary>
    /// Whether the heartbeat should tell this VM to fetch its scrub jobs: a job (or a due cleanup) that can
    /// be delivered now. Patterns need <c>K</c>, so a locked VM's scrub waits until it is unlocked.
    /// </summary>
    public async Task<bool> ScrubDueAsync(Vm vm, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary) return false;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await store.GetOwnerAsync(vm.Owner, ct).ConfigureAwait(false);
            if (state is null) return false;
            var now = clock.UtcNow;
            await ScheduleAsync(vm.Owner, now, ct).ConfigureAwait(false);
            var key = HasKey(state, vm.Name);
            return (await store.ListAsync<VaultScrubJob>(vm.Owner, ct).ConfigureAwait(false))
                .Any(j => Ownership.SameName(j.Vm, vm.Name) && Due(j, now) && (key || !NeedsKey(j)));
        }
        finally { gate.Release(); }
    }

    // ── delivery ────────────────────────────────────────────────────────────────

    /// <summary><c>GET /vms/{name}/vault/scrubs</c>: the due jobs, with their patterns computed now.</summary>
    public async Task<JsonObject> ScrubsAsync(Vm vm, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary) throw new VaultProblemException(403, "vault-child", "Child VMs have no key vault access.");
        var result = new JsonArray();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? key = null;
        try
        {
            var now = clock.UtcNow;
            var state = await store.GetOwnerAsync(vm.Owner, ct).ConfigureAwait(false);
            if (state is null) return new JsonObject { ["jobs"] = result };
            await ScheduleAsync(vm.Owner, now, ct).ConfigureAwait(false);
            key = KeyFor(state, vm.Name);
            foreach (var job in (await store.ListAsync<VaultScrubJob>(vm.Owner, ct).ConfigureAwait(false))
                         .Where(j => Ownership.SameName(j.Vm, vm.Name) && Due(j, now)).OrderBy(j => j.CreatedAt))
            {
                if (NeedsKey(job) && key is null) continue;
                var patterns = job.Items.Select<VaultSecretCopy, IReadOnlyList<string>>(item => key is null ? [] : PatternsOf(key, item)).ToArray();
                var names = new JsonArray(job.Items.Select(i => i.Name).Distinct(StringComparer.Ordinal).Select(n => (JsonNode)n).ToArray());
                if (job.Step == "scan")
                {
                    var unscannable = job.Items.Where((_, i) => patterns[i].Count == 0).Select(i => i.Name).Distinct(StringComparer.Ordinal).ToArray();
                    if (unscannable.Length == job.Items.Count || patterns.All(p => p.Count == 0))
                    {
                        await store.DeleteAsync<VaultScrubJob>(job.Id, ct).ConfigureAwait(false);
                        await RecordAsync(vm.Owner, vm.Name, $"{string.Join(", ", unscannable)} is too short to search for; not scrubbed.", true, ct).ConfigureAwait(false);
                        continue;
                    }
                    var list = new JsonArray();
                    for (var i = 0; i < patterns.Length; i++)
                        foreach (var pattern in patterns[i]) list.Add(new JsonObject { ["index"] = i, ["pattern"] = VaultProtocol.B64(pattern) });
                    await store.SaveAsync(job with { DeliveredAt = now, Unscannable = unscannable }, ct).ConfigureAwait(false);
                    result.Add(new JsonObject { ["id"] = job.Id, ["step"] = "scan", ["names"] = names, ["patterns"] = list, ["files"] = new JsonArray() });
                    continue;
                }
                var files = new JsonArray();
                foreach (var file in job.Files)
                {
                    if (file.Action == "delete")
                    {
                        files.Add(ApplyFile(file.PathB64, "delete", []));
                        // A deleted database takes its side files with it; missing ones report "missing".
                        if (file.Type == "sqlite") foreach (var side in SqliteSides) files.Add(ApplyFile(VaultProtocol.EncodePath(file.Path + side), "delete", []));
                        continue;
                    }
                    var own = file.Indexes.Where(i => i >= 0 && i < patterns.Length).Order().SelectMany(i => patterns[i]).Distinct(StringComparer.Ordinal).ToArray();
                    files.Add(ApplyFile(file.PathB64, "redact", own));
                }
                await store.SaveAsync(job with { DeliveredAt = now }, ct).ConfigureAwait(false);
                result.Add(new JsonObject { ["id"] = job.Id, ["step"] = "apply", ["names"] = names, ["patterns"] = new JsonArray(), ["files"] = files });
            }
            return new JsonObject { ["jobs"] = result };
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            gate.Release();
        }
    }

    private static JsonObject ApplyFile(string pathB64, string action, IEnumerable<string> patterns) => new()
    {
        ["path"] = pathB64, ["action"] = action,
        ["patterns"] = new JsonArray(patterns.Select(p => (JsonNode)VaultProtocol.B64(p)).ToArray()),
    };

    private static IReadOnlyList<string> PatternsOf(byte[] key, VaultSecretCopy copy)
    {
        try
        {
            var (username, secret) = VaultCrypto.OpenEntry(key, copy.Name, copy.UpdatedAt, copy.Payload);
            return VaultProtocol.Patterns(secret, username);
        }
        catch (CryptographicException) { return []; }
    }

    // ── results ─────────────────────────────────────────────────────────────────

    private sealed record Target(string Path, string PathB64, SortedSet<int> Indexes, string Type, long Size);

    /// <summary><c>POST /vms/{name}/vault/scrubs/{id}</c>: a scan's hits or an apply step's results.</summary>
    public async Task ScrubResultAsync(Vm vm, string id, JsonObject body, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary) throw new VaultProblemException(403, "vault-child", "Child VMs have no key vault access.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = (await store.ListAsync<VaultScrubJob>(vm.Owner, ct).ConfigureAwait(false)).FirstOrDefault(j => j.Id == id && Ownership.SameName(j.Vm, vm.Name))
                ?? throw NotFound("No such scrub job.");
            if (body.Str("step") != job.Step) throw Validation("step", $"This job's step is {job.Step}.");
            var names = string.Join(", ", job.Items.Select(i => i.Name).Distinct(StringComparer.Ordinal));
            var now = clock.UtcNow;
            if (job.Step == "scan" ? !body.True("complete") : body["complete"] is JsonValue c && c.TryGetValue<bool>(out var complete) && !complete)
            {
                await store.SaveAsync(job with { DeliveredAt = null, NotBefore = now + RetryDelay }, ct).ConfigureAwait(false);
                await RecordAsync(vm.Owner, vm.Name, $"Could not scrub {names} from the VM; retrying in {RetryDelay.TotalMinutes:0} minutes.", true, ct).ConfigureAwait(false);
                return;
            }
            if (job.Step == "scan") await ClassifyAsync(vm, job, body, names, now, ct).ConfigureAwait(false);
            else await TallyAsync(vm, job, body, ct).ConfigureAwait(false);
            await store.DeleteAsync<VaultScrubJob>(job.Id, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // SQLite side files fold into their database; agent logs become a redact job at once, every other
    // file waits for the user.
    private async Task ClassifyAsync(Vm vm, VaultScrubJob job, JsonObject body, string names, DateTimeOffset now, CancellationToken ct)
    {
        var hits = body["hits"] as JsonArray ?? [];
        if (hits.Count > MaxHits) throw Validation("hits", $"At most {MaxHits} hits.");
        var scannable = Enumerable.Range(0, job.Items.Count).Where(i => !job.Unscannable.Contains(job.Items[i].Name)).ToHashSet();
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var node in hits)
        {
            if (node is not JsonObject hit) continue;
            var pathB64 = hit.Str("path");
            if (VaultProtocol.DecodePath(pathB64) is not { } path) continue;
            var indexes = (hit["indexes"] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1).Where(scannable.Contains).ToArray();
            if (indexes.Length == 0) continue;
            var type = hit.Str("type") is var t && t is "sqlite" or "sqlite-aux" or "binary" ? t : "text";
            var size = hit["size"] is JsonValue s && s.TryGetValue<long>(out var bytes) && bytes >= 0 ? bytes : 0;
            var aux = type == "sqlite-aux";
            var key = aux ? VaultProtocol.DatabaseFor(path) : path;
            if (!targets.TryGetValue(key, out var target))
                targets[key] = target = new(key, aux ? VaultProtocol.EncodePath(key) : pathB64, [], aux ? "sqlite" : type, aux ? 0 : size);
            target.Indexes.UnionWith(indexes);
        }
        var agent = targets.Values.Where(t => VaultProtocol.IsAgentLog(t.Path)).OrderBy(t => t.Path, StringComparer.Ordinal).ToArray();
        var other = targets.Values.Where(t => !VaultProtocol.IsAgentLog(t.Path)).OrderBy(t => t.Path, StringComparer.Ordinal).ToArray();
        if (job.Unscannable.Count > 0)
            await RecordAsync(vm.Owner, vm.Name, $"{string.Join(", ", job.Unscannable)} is too short to search for; not scrubbed.", true, ct).ConfigureAwait(false);
        if (agent.Length > 0)
            await store.SaveAsync(new VaultScrubJob(NewId(), vm.Owner, vm.Name, "apply", "agent", job.Items,
                agent.Select(t => new VaultScrubFile(t.Path, t.PathB64, "redact", t.Indexes.ToArray(), t.Type)).ToArray(), job.Id, now, now, null, []), ct).ConfigureAwait(false);
        foreach (var target in other)
            await store.SaveAsync(new VaultFileDecision(NewId(), vm.Owner, vm.Name, target.Path, target.PathB64, target.Indexes.ToArray(), target.Type, target.Size,
                job.Items, job.Id, now), ct).ConfigureAwait(false);
        if (other.Length > 0)
            await RecordAsync(vm.Owner, vm.Name, $"{VaultRules.Files(other.Length)} on the VM still {(other.Length == 1 ? "holds" : "hold")} {names}; choose keep, redact or delete for each.", true, ct).ConfigureAwait(false);
        if (targets.Count == 0 && scannable.Count > 0)
            await RecordAsync(vm.Owner, vm.Name, $"Scanned the VM: no copies of {names} found.", false, ct).ConfigureAwait(false);
    }

    // A file counts as done when its own result is ok/partial (or already gone); side files never fail it.
    private async Task TallyAsync(Vm vm, VaultScrubJob job, JsonObject body, CancellationToken ct)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in body["results"] as JsonArray ?? [])
            if (node is JsonObject result && result.Str("path") is { Length: > 0 } path) results.TryAdd(path, result.Str("status"));
        int redacted = 0, deleted = 0;
        var failures = new List<string>(); var partial = new List<string>();
        foreach (var file in job.Files)
        {
            switch (results.GetValueOrDefault(file.PathB64))
            {
                case "ok" or "missing": break;
                case "partial": partial.Add(file.Path); break;
                default: failures.Add(file.Path); continue;
            }
            if (file.Action == "delete") deleted++; else redacted++;
        }
        var names = string.Join(", ", job.Files.SelectMany(f => f.Indexes).Distinct().Order()
            .Where(i => i >= 0 && i < job.Items.Count).Select(i => job.Items[i].Name).Distinct(StringComparer.Ordinal));
        if (job.Kind == "agent")
        {
            if (redacted > 0) await RecordAsync(vm.Owner, vm.Name, $"Redacted {names} from {VaultRules.Files(redacted)} of agent logs.", false, ct).ConfigureAwait(false);
        }
        else if (redacted + deleted > 0)
            await RecordAsync(vm.Owner, vm.Name, $"Other files with {names}: {redacted} redacted, {deleted} deleted.", false, ct).ConfigureAwait(false);
        if (partial.Count > 0)
            await RecordAsync(vm.Owner, vm.Name, $"Some data may stay in the write-ahead log of {string.Join(", ", partial)} until that app checkpoints it.", true, ct).ConfigureAwait(false);
        if (failures.Count > 0)
            await RecordAsync(vm.Owner, vm.Name, $"Could not clean {string.Join(", ", failures)}.", true, ct).ConfigureAwait(false);
    }

    // ── file decisions ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<VaultFileView>> FilesAsync(string owner, CancellationToken ct) =>
        (await store.ListAsync<VaultFileDecision>(owner, ct).ConfigureAwait(false)).OrderBy(f => f.CreatedAt).ThenBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new VaultFileView(f.Id, f.Vm, f.Path, f.Names, f.Type, f.Size, f.CreatedAt.ToUnixTimeMilliseconds())).ToArray();

    /// <summary><c>POST /vault/files/{id}</c>: keep forgets the file; redact and delete become an apply job.</summary>
    public async Task DecideFileAsync(string owner, string id, string? action, CancellationToken ct)
    {
        if (action is not ("keep" or "redact" or "delete")) throw Validation("action", "Expected keep, redact or delete.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var decision = (await store.ListAsync<VaultFileDecision>(owner, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == id)
                ?? throw NotFound("No such file.");
            await store.DeleteAsync<VaultFileDecision>(decision.Id, ct).ConfigureAwait(false);
            if (action == "keep")
            {
                await RecordAsync(owner, decision.Vm, $"Kept {decision.Path}; it still holds {string.Join(", ", decision.Names)}.", true, ct).ConfigureAwait(false);
                return;
            }
            var file = new VaultScrubFile(decision.Path, decision.PathB64, action, decision.Indexes, decision.Type);
            var open = (await store.ListAsync<VaultScrubJob>(owner, ct).ConfigureAwait(false)).FirstOrDefault(j => j.Step == "apply" && j.Kind == "decision"
                && j.DeliveredAt is null && j.Origin == decision.Origin && Ownership.SameName(j.Vm, decision.Vm));
            var now = clock.UtcNow;
            await store.SaveAsync(open is not null
                ? open with { Files = [.. open.Files.Where(f => f.PathB64 != file.PathB64), file] }
                : new VaultScrubJob(NewId(), owner, decision.Vm, "apply", "decision", decision.Items, [file], decision.Origin, now, now, null, []), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

}
