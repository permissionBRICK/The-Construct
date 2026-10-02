using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>An answered guest request (<see cref="Response"/>) or one waiting for the user (<see cref="PendingId"/>).</summary>
public sealed record VaultSubmission(JsonObject? Response, string? PendingId);

/// <summary>One pending approval as the Companion and the phone page show it.</summary>
public sealed record VaultApprovalView(string Id, string Vm, string Op, string Title, string Message, string Action,
    IReadOnlyList<string> Names, int? Uses, int? TtlSeconds, string Reason, string Source, long CreatedAt, long Deadline);

public sealed partial class VaultHostService
{
    private enum PendingState { Pending, Approved, Denied, TimedOut }

    // An approval in RAM. Its response is composed once decided; a get's value is read at pickup.
    private sealed class Pending(string id, string owner, string vm, VaultRequest request, VaultPrompt prompt,
        IReadOnlyList<string> names, int? uses, int? ttlSeconds, DateTimeOffset createdAt, DateTimeOffset deadline)
    {
        public string Id { get; } = id;
        public string Owner { get; } = owner;
        public string Vm { get; } = vm;
        public VaultRequest Request { get; } = request;
        public VaultPrompt Prompt { get; } = prompt;
        public IReadOnlyList<string> Names { get; } = names;
        public int? Uses { get; } = uses;
        public int? TtlSeconds { get; } = ttlSeconds;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset Deadline { get; } = deadline;
        public PendingState State { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
        public JsonObject? Response { get; set; }
        public string? LeaseId { get; set; }
        public bool Ready { get; set; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object pendingGate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);

    // ── guest requests ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>POST /vms/{name}/vault/requests</c>. Answers at once when no user decision is needed; otherwise
    /// records a pending approval the guest then long-polls.
    /// </summary>
    public async Task<VaultSubmission> SubmitAsync(Vm vm, JsonObject body, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary) throw new VaultProblemException(403, "vault-child", "Child VMs have no key vault access.");
        var (request, id, error) = VaultProtocol.ParseRequest(body, NewId());
        if (request is null) return new(VaultProtocol.Response(id, "invalid", error!), null);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await store.GetOwnerAsync(vm.Owner, ct).ConfigureAwait(false);
            var entries = await store.ListEntriesAsync(vm.Owner, ct).ConfigureAwait(false);
            if (request.Op is "list" or "status") return new(await ListAsync(vm, request, entries, ct).ConfigureAwait(false), null);
            if (state is not { Configured: true }) return new(VaultProtocol.Response(request.Id, "locked", NotSetUpMessage), null);
            return request.Op switch
            {
                "request" => Request(vm, request, entries),
                "get" => await GetAsync(vm, request, state, entries, ct).ConfigureAwait(false),
                "release" => new(await ReleaseAsync(vm, request, entries, ct).ConfigureAwait(false), null),
                "add" => await AddAsync(vm, request, state, entries, ct).ConfigureAwait(false),
                _ => Delete(vm, request, entries),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not VaultProblemException)
        {
            return new(VaultProtocol.Response(request.Id, "error", "The key vault could not complete the request."), null);
        }
        finally { gate.Release(); }
    }

    private async Task<JsonObject> ListAsync(Vm vm, VaultRequest request, IReadOnlyList<VaultEntry> entries, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var leases = await store.ListAsync<VaultLease>(vm.Owner, ct).ConfigureAwait(false);
        var items = new JsonArray();
        foreach (var entry in entries.Where(e => !e.Deleted && e.Payload is not null).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var lease = ActiveLease(leases, vm.Name, entry.Name, now);
            if (request.Op == "status" && lease is null) continue;
            items.Add(new JsonObject { ["name"] = entry.Name, ["description"] = entry.Description, ["hasUsername"] = entry.HasUsername,
                ["lease"] = lease is null ? null : VaultProtocol.Lease(lease) });
        }
        var response = VaultProtocol.Response(request.Id, "ok", ""); response["items"] = items; return response;
    }

    private VaultSubmission Request(Vm vm, VaultRequest request, IReadOnlyList<VaultEntry> entries)
    {
        var missing = request.Names.Where(n => Live(entries, n) is null).ToArray();
        if (missing.Length > 0) return new(VaultProtocol.Response(request.Id, "notFound", $"No secret named {string.Join(", ", missing)} in the key vault (see construct secret list)."), null);
        // A uses-only lease still ends after a day, so a forgotten grant cannot outlive the task by weeks.
        var ttl = TimeSpan.FromSeconds(request.Ttl ?? (request.Uses is null ? DefaultTtl.TotalSeconds : VaultProtocol.MaxTtl));
        var prompt = VaultPrompts.Request(vm.Name, request.Names.Select(n => Live(entries, n)!).ToArray(), request, ttl);
        return Ask(vm, request, prompt, request.Names, request.Uses, (int)ttl.TotalSeconds);
    }

    private async Task<VaultSubmission> GetAsync(Vm vm, VaultRequest request, VaultOwnerState state, IReadOnlyList<VaultEntry> entries, CancellationToken ct)
    {
        var name = request.Names[0];
        if (Live(entries, name) is not { } entry) return new(NotFound(request, name), null);
        if (!HasKey(state, vm.Name)) return new(Locked(request), null);
        var lease = ActiveLease(await store.ListAsync<VaultLease>(vm.Owner, ct).ConfigureAwait(false), vm.Name, name, clock.UtcNow);
        if (lease is null) return Ask(vm, request, VaultPrompts.Once(vm.Name, entry, request), [name], 1, (int)OnceTtl.TotalSeconds);
        return new(await ReadAsync(vm, request, lease, state, ct).ConfigureAwait(false), null);
    }

    // Counts one use and returns the value; the last use ends the lease after a grace minute.
    private async Task<JsonObject> ReadAsync(Vm vm, VaultRequest request, VaultLease lease, VaultOwnerState state, CancellationToken ct)
    {
        var name = request.Names[0];
        var entries = await store.ListEntriesAsync(vm.Owner, ct).ConfigureAwait(false);
        if (Live(entries, name) is not { } entry) return NotFound(request, name);
        var key = KeyFor(state, vm.Name);
        if (key is null) return Locked(request);
        string username, secret;
        try { (username, secret) = VaultCrypto.OpenEntry(key, entry.Name, entry.UpdatedAt, entry.Payload!); }
        catch (CryptographicException)
        {
            return VaultProtocol.Response(request.Id, "error", "The secret could not be decrypted with this vault's key; open the Key Vault in your PC's Construct Companion.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        var after = lease.UsesLeft is { } left ? lease with { UsesLeft = left - 1 } : lease;
        if (after.UsesLeft == 0) await EndAsync(after, clock.UtcNow + ExhaustedGrace, entries, ct).ConfigureAwait(false);
        else if (after != lease) await store.SaveAsync(after, ct).ConfigureAwait(false);
        var response = VaultProtocol.Response(request.Id, "ok", "");
        response["secret"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        response["username"] = username; response["lease"] = VaultProtocol.Lease(after);
        return response;
    }

    private async Task<JsonObject> ReleaseAsync(Vm vm, VaultRequest request, IReadOnlyList<VaultEntry> entries, CancellationToken ct)
    {
        var ended = new List<string>();
        foreach (var lease in (await store.ListAsync<VaultLease>(vm.Owner, ct).ConfigureAwait(false))
                     .Where(l => Ownership.SameName(l.Vm, vm.Name) && (request.All || request.Names.Contains(l.Name))).ToArray())
        {
            await EndAsync(lease, clock.UtcNow + ReleaseDelay, entries, ct).ConfigureAwait(false);
            if (!ended.Contains(lease.Name)) ended.Add(lease.Name);
        }
        if (ended.Count > 0) await RecordAsync(vm.Owner, vm.Name, $"Released {string.Join(", ", ended)}; scrubbing the VM.", false, ct).ConfigureAwait(false);
        var response = VaultProtocol.Response(request.Id, "ok", ended.Count == 0 ? "Nothing to release." : "Released.");
        response["names"] = new JsonArray(ended.Select(n => (JsonNode)n).ToArray());
        return response;
    }

    private async Task<VaultSubmission> AddAsync(Vm vm, VaultRequest request, VaultOwnerState state, IReadOnlyList<VaultEntry> entries, CancellationToken ct)
    {
        var name = request.Names[0];
        if (!HasKey(state, vm.Name)) return new(Locked(request), null);
        if (Live(entries, name) is { } existing)
        {
            if (!request.Replace) return new(Exists(request, name), null);
            var ttl = TimeSpan.FromSeconds(request.Ttl ?? DefaultTtl.TotalSeconds);
            return Ask(vm, request, VaultPrompts.Replace(vm.Name, existing, request), [name], null, (int)ttl.TotalSeconds);
        }
        return new(await StoreAsync(vm, request, ct).ConfigureAwait(false), null);
    }

    // Stores an agent's secret and leases it to that VM; a replaced value ends every lease on the old one.
    private async Task<JsonObject> StoreAsync(Vm vm, VaultRequest request, CancellationToken ct)
    {
        var name = request.Names[0];
        var state = await store.GetOwnerAsync(vm.Owner, ct).ConfigureAwait(false);
        var key = KeyFor(state, vm.Name);
        if (key is null) return Locked(request);
        try
        {
            var entries = await store.ListEntriesAsync(vm.Owner, ct).ConfigureAwait(false);
            var old = Live(entries, name);
            // Another VM (or the PC) may have added the same name meanwhile: no silent overwrite.
            if (old is not null && !request.Replace) return Exists(request, name);
            var now = clock.UtcNow;
            // The new value must win every merge against what this host has seen for the name.
            var stamp = Math.Max(now.ToUnixTimeMilliseconds(), (entries.FirstOrDefault(e => e.Name == name)?.UpdatedAt ?? 0) + 1);
            var payload = VaultCrypto.SealEntry(key, name, stamp, request.Username, request.Secret!);
            if (old is not null) await EndLeasesAsync(vm.Owner, name, Copy(old), now + ReleaseDelay, ct).ConfigureAwait(false);
            await store.SaveEntriesAsync(vm.Owner, [new VaultEntry(name, request.Description, request.Username.Length > 0, payload, stamp, "vm:" + vm.Name, false)], ct).ConfigureAwait(false);
            var ttl = TimeSpan.FromSeconds(request.Ttl ?? DefaultTtl.TotalSeconds);
            var lease = new VaultLease(NewId(), vm.Owner, vm.Name, name, null, now + ttl, now, request.Reason, "added");
            foreach (var held in (await store.ListAsync<VaultLease>(vm.Owner, ct).ConfigureAwait(false)).Where(l => Ownership.SameName(l.Vm, vm.Name) && l.Name == name))
                await store.DeleteAsync<VaultLease>(held.Id, ct).ConfigureAwait(false);
            await store.SaveAsync(lease, ct).ConfigureAwait(false);
            await RecordAsync(vm.Owner, vm.Name, $"{(old is not null ? "Replaced" : "Stored")} {name} from the VM; it holds it {VaultRules.Access(null, ttl)}.", false, ct).ConfigureAwait(false);
            var response = VaultProtocol.Response(request.Id, "ok", "Stored."); response["names"] = new JsonArray(name); response["lease"] = VaultProtocol.Lease(lease);
            return response;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private VaultSubmission Delete(Vm vm, VaultRequest request, IReadOnlyList<VaultEntry> entries)
    {
        var name = request.Names[0];
        if (Live(entries, name) is not { } entry) return new(NotFound(request, name), null);
        return Ask(vm, request, VaultPrompts.Delete(vm.Name, entry, request), [name], null, null);
    }

    // A delete writes a tombstone; it needs no K. Every VM that held the value is scrubbed of it.
    private async Task<JsonObject> TombstoneAsync(Vm vm, VaultRequest request, CancellationToken ct)
    {
        var name = request.Names[0];
        var entries = await store.ListEntriesAsync(vm.Owner, ct).ConfigureAwait(false);
        if (Live(entries, name) is not { } old) return NotFound(request, name);
        var now = clock.UtcNow;
        await EndLeasesAsync(vm.Owner, name, Copy(old), now + ReleaseDelay, ct).ConfigureAwait(false);
        var stamp = Math.Max(now.ToUnixTimeMilliseconds(), old.UpdatedAt + 1);
        await store.SaveEntriesAsync(vm.Owner, [new VaultEntry(name, "", false, null, stamp, "vm:" + vm.Name, true)], ct).ConfigureAwait(false);
        await RecordAsync(vm.Owner, vm.Name, $"Deleted {name} at the VM's request.", false, ct).ConfigureAwait(false);
        var response = VaultProtocol.Response(request.Id, "ok", "Deleted."); response["names"] = new JsonArray(name); return response;
    }

    private static JsonObject NotFound(VaultRequest request, string name) =>
        VaultProtocol.Response(request.Id, "notFound", $"No secret named {name} in the key vault (see construct secret list).");
    private static JsonObject Exists(VaultRequest request, string name) =>
        VaultProtocol.Response(request.Id, "exists", $"A secret named {name} already exists. Pick another name, or pass --replace (needs the user's approval).");
    private static JsonObject Locked(VaultRequest request) => VaultProtocol.Response(request.Id, "locked", LockedMessage);
    private static JsonObject Refused(VaultRequest request, bool timedOut) => VaultProtocol.Response(request.Id, "denied",
        timedOut ? "No answer from the user before the request timed out." : "The user denied the request.");
    private static VaultLease? ActiveLease(IEnumerable<VaultLease> leases, string vm, string name, DateTimeOffset now) =>
        leases.FirstOrDefault(l => Ownership.SameName(l.Vm, vm) && l.Name == name && LiveLease(l, now));

    // ── pending approvals ───────────────────────────────────────────────────────

    private VaultSubmission Ask(Vm vm, VaultRequest request, VaultPrompt prompt, IReadOnlyList<string> names, int? uses, int? ttlSeconds)
    {
        var now = clock.UtcNow;
        var deadline = request.Deadline is { } due ? (due - now > MaxWait ? now + MaxWait : due) : now + DefaultWait;
        if (deadline <= now) return new(Refused(request, true), null);
        lock (pendingGate)
        {
            if (pending.Values.Count(p => p.State == PendingState.Pending && Ownership.SameName(p.Vm, vm.Name)) >= MaxPendingPerVm)
                return new(VaultProtocol.Response(request.Id, "error", "Too many requests from this VM are waiting for an answer."), null);
            var record = new Pending(NewId(), vm.Owner, vm.Name, request, prompt, names, uses, ttlSeconds, now, deadline);
            pending[record.Id] = record;
            return new(null, record.Id);
        }
    }

    /// <summary>The pending approvals of this user's VMs, oldest first.</summary>
    public IReadOnlyList<VaultApprovalView> Approvals(string owner)
    {
        var now = clock.UtcNow;
        lock (pendingGate)
        {
            ExpireLocked(now);
            return pending.Values.Where(p => p.State == PendingState.Pending && Ownership.SameName(p.Owner, owner)).OrderBy(p => p.CreatedAt)
                .Select(p => new VaultApprovalView(p.Id, p.Vm, p.Request.Op, p.Prompt.Title, p.Prompt.Message, p.Prompt.Action, p.Names,
                    p.Uses, p.TtlSeconds, p.Request.Reason, p.Request.Source, p.CreatedAt.ToUnixTimeMilliseconds(), p.Deadline.ToUnixTimeMilliseconds()))
                .ToArray();
        }
    }

    /// <summary><c>POST /vault/approvals/{id}</c>: the first answer wins; later ones get 409.</summary>
    public async Task DecideAsync(string owner, string id, bool approve, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Pending record;
            lock (pendingGate)
            {
                ExpireLocked(clock.UtcNow);
                if (!pending.TryGetValue(id, out record!) || !Ownership.SameName(record.Owner, owner)) throw NotFound("No such request.");
                if (record.State != PendingState.Pending)
                    throw new VaultProblemException(409, "already-decided", record.State == PendingState.TimedOut ? "The request timed out." : "The request was already answered.");
                record.State = approve ? PendingState.Approved : PendingState.Denied;
                record.DecidedAt = clock.UtcNow;
            }
            if (!approve)
            {
                Finish(record, Refused(record.Request, false));
                await RecordAsync(record.Owner, record.Vm, $"Denied the VM's {record.Request.Op} request for {string.Join(", ", record.Names)}.", false, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            // The user said yes: whatever happens to this HTTP call, the effects of the answer complete.
            try { await ApplyAsync(record, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception)
            {
                Finish(record, VaultProtocol.Response(record.Request.Id, "error", "The key vault could not complete the request."));
            }
        }
        finally { gate.Release(); }
    }

    private async Task ApplyAsync(Pending record, CancellationToken ct)
    {
        var vm = await vms.GetAsync(record.Vm, ct).ConfigureAwait(false);
        var request = record.Request;
        if (vm is null || vm.Deleting) { Finish(record, VaultProtocol.Response(request.Id, "error", "The VM is being deleted.")); return; }
        switch (request.Op)
        {
            case "request":
            {
                // The lease runs from the approval, not from the request: a slow answer must not shorten it.
                var now = clock.UtcNow; var ttl = TimeSpan.FromSeconds(record.TtlSeconds ?? DefaultTtl.TotalSeconds);
                var entries = await store.ListEntriesAsync(vm.Owner, ct).ConfigureAwait(false);
                var leases = await store.ListAsync<VaultLease>(vm.Owner, ct).ConfigureAwait(false);
                VaultLease? granted = null; var names = new List<string>();
                foreach (var name in request.Names.Where(n => Live(entries, n) is not null))
                {
                    foreach (var held in leases.Where(l => Ownership.SameName(l.Vm, vm.Name) && l.Name == name))
                        await store.DeleteAsync<VaultLease>(held.Id, ct).ConfigureAwait(false);
                    granted = new(NewId(), vm.Owner, vm.Name, name, request.Uses, now + ttl, now, request.Reason, "approved");
                    await store.SaveAsync(granted, ct).ConfigureAwait(false); names.Add(name);
                }
                await RecordAsync(vm.Owner, vm.Name, $"Access to {string.Join(", ", names)} approved ({VaultRules.Access(request.Uses, ttl)}).", false, ct).ConfigureAwait(false);
                var response = VaultProtocol.Response(request.Id, "ok", "Approved."); response["names"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray());
                if (granted is not null) response["lease"] = VaultProtocol.Lease(granted);
                Finish(record, response);
                return;
            }
            case "get":
            {
                var name = request.Names[0]; var now = clock.UtcNow;
                foreach (var held in (await store.ListAsync<VaultLease>(vm.Owner, ct).ConfigureAwait(false)).Where(l => Ownership.SameName(l.Vm, vm.Name) && l.Name == name))
                    await store.DeleteAsync<VaultLease>(held.Id, ct).ConfigureAwait(false);
                var once = new VaultLease(NewId(), vm.Owner, vm.Name, name, 1, now + OnceTtl, now, request.Reason, "once");
                await store.SaveAsync(once, ct).ConfigureAwait(false);
                await RecordAsync(vm.Owner, vm.Name, $"One-time access to {name} approved.", false, ct).ConfigureAwait(false);
                // The value is read when the guest picks the answer up.
                lock (pendingGate) { record.LeaseId = once.Id; record.Ready = true; }
                record.Done.TrySetResult();
                return;
            }
            case "add":
                Finish(record, await StoreAsync(vm, request, ct).ConfigureAwait(false));
                return;
            default:
                Finish(record, await TombstoneAsync(vm, request, ct).ConfigureAwait(false));
                return;
        }
    }

    private void Finish(Pending record, JsonObject response)
    {
        lock (pendingGate) { record.Response = response; record.Ready = true; record.DecidedAt ??= clock.UtcNow; }
        record.Done.TrySetResult();
    }

    /// <summary>
    /// <c>GET /vms/{name}/vault/requests/{id}?wait=…</c>: the response document once decided (the first
    /// retrieval consumes it), or null while still pending after waiting at most <paramref name="wait"/>.
    /// </summary>
    public async Task<JsonObject?> PollAsync(Vm vm, string id, TimeSpan wait, CancellationToken ct)
    {
        Pending? record;
        lock (pendingGate)
        {
            ExpireLocked(clock.UtcNow);
            if (!pending.TryGetValue(id, out record) || !Ownership.SameName(record.Vm, vm.Name)) throw NotFound("No such request.");
        }
        if (!record.Ready)
        {
            var left = record.Deadline - clock.UtcNow;
            var span = wait < left ? wait : left;
            if (span > TimeSpan.Zero)
            {
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(span, timer.Token);
                await Task.WhenAny(record.Done.Task, delay).ConfigureAwait(false);
                await timer.CancelAsync().ConfigureAwait(false);
                // A guest that hung up must not consume its answer: it asks again.
                if (ct.IsCancellationRequested) return null;
            }
            lock (pendingGate)
            {
                ExpireLocked(clock.UtcNow);
                if (!record.Ready) return null;
            }
        }
        lock (pendingGate) { if (!pending.Remove(id)) throw NotFound("No such request."); }
        if (record.Response is { } response) return response;
        // An approved one-time get: the value is read now, under the gate.
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var state = await store.GetOwnerAsync(vm.Owner, CancellationToken.None).ConfigureAwait(false);
            var lease = (await store.ListAsync<VaultLease>(vm.Owner, CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(l => l.Id == record.LeaseId);
            if (lease is null || !LiveLease(lease, clock.UtcNow))
                return VaultProtocol.Response(record.Request.Id, "denied", $"Access to {record.Request.Names[0]} was revoked on the PC.");
            if (state is not { Configured: true }) return VaultProtocol.Response(record.Request.Id, "locked", NotSetUpMessage);
            return await ReadAsync(vm, record.Request, lease, state, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { return VaultProtocol.Response(record.Request.Id, "error", "The key vault could not complete the request."); }
        finally { gate.Release(); }
    }

    // Deadlines close unanswered requests as denied; answers nobody picked up are forgotten.
    private void ExpireLocked(DateTimeOffset now)
    {
        foreach (var record in pending.Values.ToArray())
        {
            if (record.State == PendingState.Pending && record.Deadline <= now)
            {
                record.State = PendingState.TimedOut; record.DecidedAt = now;
                record.Response = Refused(record.Request, true); record.Ready = true;
                record.Done.TrySetResult();
            }
            else if (record.DecidedAt is { } decided && decided + PickupGrace <= now) pending.Remove(record.Id);
        }
    }

    private void CancelPending(string vmName, string message)
    {
        lock (pendingGate)
        {
            foreach (var record in pending.Values.Where(p => Ownership.SameName(p.Vm, vmName)).ToArray())
            {
                pending.Remove(record.Id);
                if (record.State != PendingState.Pending) continue;
                record.State = PendingState.Denied; record.DecidedAt = clock.UtcNow;
                record.Response = VaultProtocol.Response(record.Request.Id, "denied", message); record.Ready = true;
                record.Done.TrySetResult();
            }
        }
    }
}
