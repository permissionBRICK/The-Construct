using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Notifications;
namespace Construct.Companion.Core.Vault;

// The key vault's single owner: answers VM requests, asks the user, counts lease uses, expires
// leases and scrubs every VM a lease ended on. All document changes run under gate and are saved
// before the lock is released; dialogs and SSH never run under it. VaultService.Hosts.cs holds the
// document side of the host sync (VaultHosts does the network side), VaultService.Approvals.cs the
// pending approvals that the tray pop-out and other apps answer.
public sealed partial class VaultService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1), OnceTtl = TimeSpan.FromMinutes(10),
        ReleaseDelay = TimeSpan.FromSeconds(5), ExhaustedGrace = TimeSpan.FromMinutes(1), RetryDelay = TimeSpan.FromMinutes(5), Tick = TimeSpan.FromSeconds(10);
    private const int ActivityLimit = 200;
    private readonly VaultStore store;
    private readonly IPrompts prompts;
    private readonly IToastRaiser toasts;
    private readonly IClock clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim decisions = new(1);
    private readonly Dictionary<string, ISshTransport> attached = new(StringComparer.Ordinal);
    private readonly HashSet<string> cleaning = new(StringComparer.Ordinal);
    private readonly List<Task> running = [];
    private readonly List<VaultActivity> activity = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource stop = new();
    private VaultDocument? document;
    private string? unavailable;
    public event Action? Changed;
    // Secrets or tombstones changed on this PC (window, local VM request): the hosts need a sync.
    public event Action? EntriesChanged;

    public VaultService(VaultStore store, IPrompts prompts, IToastRaiser toasts, IClock clock)
    {
        this.store = store; this.prompts = prompts; this.toasts = toasts; this.clock = clock;
        try { document = store.Load(); }
        catch (VaultUnavailableException e) { unavailable = e.Message; }
        // A phase-1 vault gains an install id; its entries become this PC's writes at their old time.
        if (document is not null && Migrate(document) && document.Secrets.Count > 0)
            try { store.Save(document); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* saved with the next change */ }
    }
    public string? Unavailable { get { lock (gate) return unavailable; } }
    public string StorePath => store.PathName;

    // ── VM requests ─────────────────────────────────────────────────────────────
    public async Task<JsonObject> HandleAsync(string instance, VaultRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Unavailable is { } problem) return VaultProtocol.Response(request.Id, "error", problem + " Open the Key Vault on the PC to resolve it.");
            return request.Op switch
            {
                "list" => List(instance, request, false), "status" => List(instance, request, true),
                "request" => await RequestAsync(instance, request, cancellationToken).ConfigureAwait(false),
                "get" => await GetAsync(instance, request, cancellationToken).ConfigureAwait(false),
                "release" => Release(instance, request),
                "add" => await AddAsync(instance, request, cancellationToken).ConfigureAwait(false),
                "delete" => await DeleteAsync(instance, request, cancellationToken).ConfigureAwait(false),
                "autoApprove" => await AutoApproveAsync(instance, request, cancellationToken).ConfigureAwait(false),
                _ => VaultProtocol.Response(request.Id, "invalid", "Unknown operation.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (VaultUnavailableException e) { return VaultProtocol.Response(request.Id, "error", e.Message); }
        catch (Exception) { return VaultProtocol.Response(request.Id, "error", "The key vault could not complete the request."); }
    }

    private JsonObject List(string instance, VaultRequest request, bool leasedOnly)
    {
        var now = clock.UtcNow; var items = new JsonArray();
        lock (gate)
        {
            foreach (var entry in Document.Secrets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var lease = ActiveLease(instance, entry.Name, now);
                // status: what this VM can read now, which includes every auto-approved secret.
                if (leasedOnly && lease is null && !entry.AutoApprove) continue;
                items.Add(new JsonObject { ["name"] = entry.Name, ["description"] = entry.Description, ["hasUsername"] = entry.Username.Length > 0,
                    ["autoApprove"] = entry.AutoApprove, ["lease"] = lease is null ? null : VaultProtocol.Lease(lease) });
            }
        }
        var response = VaultProtocol.Response(request.Id, "ok", ""); response["items"] = items; return response;
    }

    private async Task<JsonObject> RequestAsync(string instance, VaultRequest request, CancellationToken token)
    {
        string[] missing, auto, asked, described;
        lock (gate)
        {
            missing = request.Names.Where(n => Find(n) is null).ToArray();
            auto = request.Names.Where(n => Find(n) is { AutoApprove: true }).ToArray();
            asked = request.Names.Except(missing).Except(auto).ToArray();
            described = asked.Select(n => Bullet(Find(n)!)).ToArray();
        }
        if (missing.Length > 0) return VaultProtocol.Response(request.Id, "notFound", $"No secret named {string.Join(", ", missing)} in the key vault (see construct secret list).");
        // Auto-approved secrets need neither the user nor a lease: every get of them is answered at once.
        if (auto.Length > 0) Record(instance, $"Access to {string.Join(", ", auto)} auto-approved.");
        if (asked.Length == 0) return Granted(request, auto, auto, null);
        // A uses-only lease still ends after a day, so a forgotten grant cannot outlive the task by weeks.
        var ttl = TimeSpan.FromSeconds(request.Ttl ?? (request.Uses is null ? DefaultTtl.TotalSeconds : VaultProtocol.MaxTtl));
        var message = $"The VM “{instance}” asks for access to:\n{string.Join('\n', described)}\n\nAccess: {Access(request.Uses, clock.UtcNow + ttl, clock.UtcNow)}"
            + Footer(request);
        var answer = await AskAsync(instance, request, new("Key vault — access request", message), token, asked).ConfigureAwait(false);
        if (answer != Approval.Approved) return Refused(request, answer, auto);
        // The lease runs from the approval, not from the request: a slow answer must not shorten it.
        var now = clock.UtcNow; var expires = now + ttl;
        VaultLease? granted = null; var names = new List<string>();
        Mutate(d =>
        {
            foreach (var name in asked.Where(n => Find(n) is not null))
            {
                d.Leases.RemoveAll(l => l.Instance == instance && l.Name == name);
                granted = new(NewId(), instance, name, request.Uses, expires, clock.UtcNow, request.Reason, "approved");
                d.Leases.Add(granted); names.Add(name);
            }
        });
        Record(instance, $"Access to {string.Join(", ", names)} approved ({Access(request.Uses, expires, now)}).");
        return Granted(request, auto.Concat(names), auto, granted);
    }
    private static JsonObject Granted(VaultRequest request, IEnumerable<string> names, IEnumerable<string> auto, VaultLease? lease)
    {
        var response = VaultProtocol.Response(request.Id, "ok", "Approved."); response["names"] = Names(names); response["auto"] = Names(auto);
        if (lease is not null) response["lease"] = VaultProtocol.Lease(lease);
        return response;
    }

    private async Task<JsonObject> GetAsync(string instance, VaultRequest request, CancellationToken token)
    {
        var name = request.Names[0]; string bullet; VaultLease? lease; VaultEntry found;
        lock (gate)
        {
            if (Find(name) is not { } entry) return NotFound(request, name);
            found = entry; bullet = Bullet(entry); lease = ActiveLease(instance, name, clock.UtcNow);
        }
        // Auto-approved and not leased: the value at once, without a lease or a use to count.
        if (lease is null && found.AutoApprove) return Value(request, found, null);
        if (lease is null)
        {
            var answer = await AskAsync(instance, request, new("Key vault — one-time access", $"The VM “{instance}” asks to read this secret once:\n{bullet}" + Footer(request), "Allow once"),
                token).ConfigureAwait(false);
            if (answer != Approval.Approved) return Refused(request, answer);
            var once = new VaultLease(NewId(), instance, name, 1, clock.UtcNow + OnceTtl, clock.UtcNow, request.Reason, "once");
            Mutate(d => { d.Leases.RemoveAll(l => l.Instance == instance && l.Name == name); d.Leases.Add(once); });
            Record(instance, $"One-time access to {name} approved.");
            lease = once;
        }
        VaultEntry? value = null; VaultLease? after = null;
        Mutate(d =>
        {
            var current = d.Leases.FirstOrDefault(l => l.Id == lease.Id);
            value = Find(name);
            if (current is null || value is null) return;
            after = current.UsesLeft is { } left ? current with { UsesLeft = left - 1 } : current;
            d.Leases[d.Leases.IndexOf(current)] = after;
            if (after.UsesLeft == 0) End(d, after, clock.UtcNow + ExhaustedGrace);
        });
        if (value is null) return NotFound(request, name);
        if (after is null) return VaultProtocol.Response(request.Id, "denied", $"Access to {name} was revoked on the PC.");
        return Value(request, value, after);
    }
    private static JsonObject Value(VaultRequest request, VaultEntry entry, VaultLease? lease)
    {
        var response = VaultProtocol.Response(request.Id, "ok", "");
        response["secret"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Value.Reveal()));
        response["username"] = entry.Username; response["lease"] = lease is null ? null : VaultProtocol.Lease(lease);
        return response;
    }

    private JsonObject Release(string instance, VaultRequest request)
    {
        var ended = new List<string>(); var unscrubbed = new List<string>();
        Mutate(d =>
        {
            foreach (var lease in d.Leases.Where(l => l.Instance == instance && (request.All || request.Names.Contains(l.Name))).ToArray())
            {
                End(d, lease, clock.UtcNow + ReleaseDelay);
                if (ended.Contains(lease.Name)) continue;
                ended.Add(lease.Name);
                if (Find(lease.Name) is { AutoApprove: true }) unscrubbed.Add(lease.Name);
            }
        });
        var scrubbed = ended.Except(unscrubbed).ToArray();
        if (scrubbed.Length > 0) Record(instance, $"Released {string.Join(", ", scrubbed)}; scrubbing the VM.");
        if (unscrubbed.Count > 0) Record(instance, $"Released {string.Join(", ", unscrubbed)} (auto-approved, no scrub).");
        var response = VaultProtocol.Response(request.Id, "ok", ended.Count == 0 ? "Nothing to release." : "Released.");
        response["names"] = Names(ended); response["auto"] = Names(unscrubbed);
        return response;
    }

    private async Task<JsonObject> AddAsync(string instance, VaultRequest request, CancellationToken token)
    {
        var name = request.Names[0]; string? bullet = null; var noted = false;
        lock (gate) if (Find(name) is { } existing) { bullet = Bullet(existing); noted = request.AutoApprove == true && !existing.AutoApprove; }
        if (bullet is not null)
        {
            if (!request.Replace) return VaultProtocol.Response(request.Id, "exists", $"A secret named {name} already exists. Pick another name, or pass --replace (needs the user's approval).");
            var answer = await AskAsync(instance, request, new("Key vault — replace secret", $"The VM “{instance}” wants to replace the value of:\n{bullet}\n\nNew description: {request.Description}"
                + (noted ? "\n\nIt also becomes auto-approved: agents on all your VMs read it without asking you, and no VM is scrubbed of it." : "") + Footer(request), "Replace"),
                token).ConfigureAwait(false);
            if (answer != Approval.Approved) return Refused(request, answer);
        }
        var now = clock.UtcNow; var expires = now + TimeSpan.FromSeconds(request.Ttl ?? DefaultTtl.TotalSeconds);
        VaultLease? lease = null;
        bool replaced = false, taken = false, auto = false;
        Mutate(d =>
        {
            var old = Find(name);
            // Another VM may have added the same name while this one was checked: no silent overwrite.
            if (old is not null && !request.Replace) { taken = true; return; }
            // The agent picks the setting of a new secret; a replacement keeps the old one unless the user's
            // approval covered switching it on.
            auto = old is null ? request.AutoApprove == true
                : request.AutoApprove switch { true => old.AutoApprove || noted, false => false, null => old.AutoApprove };
            if (old is not null)
            {
                // Every VM that saw the old value gets scrubbed of it (unless the old value was auto-approved).
                foreach (var held in d.Leases.Where(l => l.Name == name).ToArray()) End(d, held, now + ReleaseDelay);
                d.Secrets.Remove(old); replaced = true;
            }
            d.Secrets.Add(new(name, request.Description, request.Username, request.Value!, old?.CreatedAt ?? now, Stamp(old?.UpdatedAt, Buried(d, name)), "agent:" + instance, Writer, auto));
            d.Tombstones.RemoveAll(t => t.Name == name);
            d.Leases.RemoveAll(l => l.Instance == instance && l.Name == name);
            // An auto-approved secret needs no lease: this VM reads it at any time.
            if (!auto) { lease = new(NewId(), instance, name, null, expires, now, request.Reason, "added"); d.Leases.Add(lease); }
        });
        if (taken) return VaultProtocol.Response(request.Id, "exists", $"A secret named {name} already exists. Pick another name, or pass --replace (needs the user's approval).");
        EntriesChanged?.Invoke();
        Record(instance, $"{(replaced ? "Replaced" : "Stored")} {name} from the VM; " + (auto ? "auto-approved." : $"it holds it {Access(null, expires, now)}."));
        await ToastAsync(replaced ? "Secret replaced" : "Secret stored", $"The VM “{instance}” {(replaced ? "replaced" : "stored")} “{name}” in the key vault.", token).ConfigureAwait(false);
        var response = VaultProtocol.Response(request.Id, "ok", "Stored."); response["names"] = new JsonArray(name); response["autoApprove"] = auto;
        if (lease is not null) response["lease"] = VaultProtocol.Lease(lease);
        return response;
    }

    // Switching auto-approve on lets every VM read the secret without the user, so it needs their approval;
    // switching it off only takes that away again.
    private async Task<JsonObject> AutoApproveAsync(string instance, VaultRequest request, CancellationToken token)
    {
        var name = request.Names[0]; var on = request.AutoApprove == true; string bullet;
        lock (gate)
        {
            if (Find(name) is not { } entry) return NotFound(request, name);
            if (entry.AutoApprove == on) return Switched(request, name, on, on ? "Already auto-approved." : "Already needs approval.");
            bullet = Bullet(entry);
        }
        if (on)
        {
            var answer = await AskAsync(instance, request, new("Key vault — auto-approve secret",
                $"The VM “{instance}” asks to auto-approve this secret:\n{bullet}\n\nAgents on all your VMs could then read it at any time without asking you, and no VM is scrubbed of it any more."
                + Footer(request), "Auto-approve"),
                token).ConfigureAwait(false);
            if (answer != Approval.Approved) return Refused(request, answer);
        }
        if (!SetFlag(name, on)) return NotFound(request, name);
        Record(instance, on ? $"Auto-approve for {name} approved at the VM's request." : $"{name} needs your approval again (the VM turned auto-approve off).");
        return Switched(request, name, on, on ? "Auto-approved." : "Needs approval again.");
    }
    private static JsonObject Switched(VaultRequest request, string name, bool on, string message)
    {
        var response = VaultProtocol.Response(request.Id, "ok", message); response["names"] = new JsonArray(name); response["autoApprove"] = on;
        return response;
    }

    private async Task<JsonObject> DeleteAsync(string instance, VaultRequest request, CancellationToken token)
    {
        var name = request.Names[0]; string bullet;
        lock (gate) { if (Find(name) is not { } entry) return NotFound(request, name); bullet = Bullet(entry); }
        var answer = await AskAsync(instance, request, new("Key vault — delete secret", $"The VM “{instance}” asks to delete this secret from the key vault:\n{bullet}" + Footer(request), "Delete"),
            token).ConfigureAwait(false);
        if (answer != Approval.Approved) return Refused(request, answer);
        var deleted = false;
        Mutate(d => deleted = RemoveSecret(d, name));
        if (!deleted) return NotFound(request, name);
        EntriesChanged?.Invoke();
        Record(instance, $"Deleted {name} at the VM's request.");
        var response = VaultProtocol.Response(request.Id, "ok", "Deleted."); response["names"] = new JsonArray(name); return response;
    }

    private static JsonObject NotFound(VaultRequest request, string name) => VaultProtocol.Response(request.Id, "notFound", $"No secret named {name} in the key vault (see construct secret list).");
    private static JsonObject Refused(VaultRequest request, Approval answer, IReadOnlyList<string>? auto = null) => VaultProtocol.Response(request.Id, "denied",
        (answer == Approval.TimedOut ? "No answer from the user before the request timed out." : "The user denied the request.")
        + (auto is { Count: > 0 } ? $" {string.Join(", ", auto)} need{(auto.Count == 1 ? "s" : "")} no approval: read {(auto.Count == 1 ? "it" : "them")} with construct secret get." : ""));
    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode)n).ToArray());
    private static string Bullet(VaultEntry entry) => $"  • {entry.Name}{(entry.Description.Length > 0 ? " — " + entry.Description : "")}{(entry.Username.Length > 0 ? " (with username)" : "")}";
    private static string Footer(VaultRequest request) => (request.Reason.Length > 0 ? $"\nReason given: “{request.Reason}”" : "") + (request.Source.Length > 0 ? $"\nRequested by {request.Source}" : "");

    // ── the PC side: the Key Vault window ──────────────────────────────────────
    public IReadOnlyList<VaultSecretView> Secrets()
    {
        var now = clock.UtcNow;
        lock (gate) return document is null ? [] : document.Secrets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new VaultSecretView(s.Name, s.Description, s.Username, s.Origin, s.UpdatedAt, document.Leases.Count(l => l.Name == s.Name && Live(l, now)), s.AutoApprove)).ToArray();
    }
    public IReadOnlyList<VaultLeaseView> Leases()
    {
        var now = clock.UtcNow;
        lock (gate) return document is null ? [] : document.Leases.Where(l => Live(l, now)).OrderBy(l => l.ExpiresAt)
            .Select(l => new VaultLeaseView(l.Id, l.Instance, l.Name, l.UsesLeft, l.ExpiresAt, l.Reason, l.Origin)).ToArray();
    }
    public IReadOnlyList<(string Instance, string Name, DateTimeOffset DueAt)> PendingCleanups()
    { lock (gate) return document is null ? [] : document.Cleanups.Select(c => (c.Instance, c.Name, c.DueAt)).ToArray(); }
    public IReadOnlyList<VaultActivity> Activity() { lock (gate) return activity.ToArray(); }
    public Secret? Reveal(string name) { lock (gate) return document is null ? null : Find(name)?.Value; }
    public string Username(string name) { lock (gate) return document is null ? "" : Find(name)?.Username ?? ""; }

    // Add (original null) or edit. A rename or a new value ends every lease on the old one, so the VMs
    // that held it are scrubbed of it.
    public void Save(VaultSecretInput input, string? original = null)
    {
        if (!VaultProtocol.IsValidName(input.Name)) throw new ArgumentException("Names use letters, digits, '.', '_' and '-' (at most 64, starting with a letter or digit).");
        if (input.Value is { } v && (v.Reveal().Length == 0 || Encoding.UTF8.GetByteCount(v.Reveal()) > VaultProtocol.MaxSecretBytes))
            throw new ArgumentException($"The secret must be between 1 byte and {VaultProtocol.MaxSecretBytes / 1024} KiB.");
        var description = Runtime.RuntimeJson.Sanitize(input.Description, VaultProtocol.MaxText); var username = Runtime.RuntimeJson.Sanitize(input.Username, VaultProtocol.MaxText);
        Mutate(d =>
        {
            var old = original is null ? null : Find(original) ?? throw new ArgumentException("That secret no longer exists.");
            if (!string.Equals(input.Name, original, StringComparison.Ordinal) && Find(input.Name) is not null) throw new ArgumentException($"A secret named {input.Name} already exists.");
            if (old is null && input.Value is null) throw new ArgumentException("Enter the secret value.");
            var now = clock.UtcNow; var stamp = Stamp(old?.UpdatedAt, Buried(d, input.Name));
            if (old is not null)
            {
                if (old.Name != input.Name || input.Value is not null && input.Value.Reveal() != old.Value.Reveal())
                    foreach (var held in d.Leases.Where(l => l.Name == old.Name).ToArray()) End(d, held, now + ReleaseDelay);
                d.Secrets.Remove(old);
                // A rename deletes the old name everywhere: hosts must not keep (or bring back) it.
                if (old.Name != input.Name) Bury(d, old.Name, Stamp(old.UpdatedAt));
            }
            d.Tombstones.RemoveAll(t => t.Name == input.Name);
            d.Secrets.Add(new(input.Name, description, username, input.Value ?? old!.Value, old?.CreatedAt ?? now, stamp, old?.Origin ?? "user", Writer, old?.AutoApprove ?? false));
        });
        EntriesChanged?.Invoke();
    }
    // The window's switch. False when the secret is gone.
    public bool SetAutoApprove(string name, bool on)
    {
        if (!SetFlag(name, on)) return false;
        Record("", on ? $"{name} is auto-approved now: agents read it without asking, and VMs are not scrubbed of it." : $"{name} needs your approval again.");
        return true;
    }
    // A new stamp, so the setting reaches every host like any other edit. True when the secret exists.
    private bool SetFlag(string name, bool on)
    {
        bool found = false, changed = false;
        Mutate(d =>
        {
            if (Find(name) is not { } entry) return;
            found = true;
            if (entry.AutoApprove == on) return;
            d.Secrets[d.Secrets.IndexOf(entry)] = entry with { AutoApprove = on, UpdatedAt = Stamp(entry.UpdatedAt, Buried(d, name)), UpdatedBy = Writer };
            changed = true;
        });
        if (changed) EntriesChanged?.Invoke();
        return found;
    }
    public void Delete(string name)
    {
        var deleted = false; Mutate(d => deleted = RemoveSecret(d, name));
        if (deleted) { EntriesChanged?.Invoke(); Record("", $"Deleted {name}."); }
    }
    public void Revoke(string leaseId)
    {
        VaultLease? ended = null;
        var scrub = false;
        Mutate(d => { if (d.Leases.FirstOrDefault(l => l.Id == leaseId) is { } lease) { End(d, lease, clock.UtcNow); ended = lease; scrub = Find(lease.Name) is { AutoApprove: false }; } });
        if (ended is not null) Record(ended.Instance, $"Revoked access to {ended.Name}" + (scrub ? "; scrubbing the VM." : "."));
    }
    public void DiscardCleanups(string instance)
    {
        Mutate(d => d.Cleanups.RemoveAll(c => c.Instance == instance));
        Record(instance, "Pending scrubs discarded.", true);
    }
    // Moves an undecryptable vault aside and starts empty; the old file stays for recovery.
    public string ResetUnreadable()
    {
        string moved;
        lock (gate)
        {
            if (unavailable is null) throw new InvalidOperationException("The key vault is readable.");
            moved = store.QuarantineUnreadable(clock.UtcNow); document = new() { InstallId = NewId() }; store.Save(document); unavailable = null;
        }
        Changed?.Invoke(); return moved;
    }

    // ── runtime: connections, expiry and scrubbing ─────────────────────────────
    // A VM's broker attaches while the VM is online; scrubs only run against an attached VM.
    public IDisposable Attach(string instance, ISshTransport ssh)
    {
        lock (gate) attached[instance] = ssh;
        wake.Writer.TryWrite(true);
        return new Attachment(this, instance, ssh);
    }
    private sealed class Attachment(VaultService owner, string instance, ISshTransport ssh) : IDisposable
    {
        public void Dispose() { lock (owner.gate) if (owner.attached.TryGetValue(instance, out var current) && ReferenceEquals(current, ssh)) owner.attached.Remove(instance); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { Sweep(); } catch (VaultUnavailableException) { } catch (IOException) { }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var delay = clock.DelayAsync(Tick, wait.Token); var signal = wake.Reader.WaitToReadAsync(wait.Token).AsTask();
                await Task.WhenAny(delay, signal).ConfigureAwait(false); await wait.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(delay, signal).ConfigureAwait(false); } catch (OperationCanceledException) { }
                while (wake.Reader.TryRead(out _)) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    // Expires leases and old tombstones, then starts at most one scrub per attached VM for its due cleanups.
    public void Sweep()
    {
        var now = clock.UtcNow;
        bool buried; lock (gate) buried = document is not null && document.Tombstones.Any(t => t.UpdatedAt <= now - VaultSync.TombstoneLifetime);
        if (buried) Mutate(d => d.Tombstones.RemoveAll(t => t.UpdatedAt <= now - VaultSync.TombstoneLifetime));
        bool expired; lock (gate) expired = document is not null && document.Leases.Any(l => l.ExpiresAt <= now);
        if (expired)
        {
            var names = new List<(string, string, bool)>();
            Mutate(d =>
            {
                foreach (var lease in d.Leases.Where(l => l.ExpiresAt <= now).ToArray())
                { End(d, lease, now); names.Add((lease.Instance, lease.Name, Find(lease.Name) is { AutoApprove: false })); }
            });
            foreach (var (instance, name, scrub) in names) Record(instance, $"Access to {name} expired" + (scrub ? "; scrubbing the VM." : "."));
        }
        // A secret switched to auto-approve is no longer scrubbed, also where a scrub was already waiting.
        bool unscrubbed; lock (gate) unscrubbed = document is not null && document.Cleanups.Any(c => Find(c.Name) is { AutoApprove: true });
        if (unscrubbed) Mutate(d => d.Cleanups.RemoveAll(c => Find(c.Name) is { AutoApprove: true }));
        lock (gate)
        {
            if (document is null || stop.IsCancellationRequested) return;
            running.RemoveAll(t => t.IsCompleted);
            foreach (var (instance, ssh) in attached)
            {
                if (cleaning.Contains(instance)) continue;
                // Still (or again) leased with the same value: that lease's own end scrubs it.
                var due = document.Cleanups.Where(c => c.Instance == instance && c.DueAt <= now
                    && !document.Leases.Any(l => l.Instance == instance && l.Name == c.Name && Live(l, now) && Find(c.Name)?.Value.Reveal() == c.Value.Reveal())).ToArray();
                if (due.Length == 0) continue;
                cleaning.Add(instance);
                running.Add(Task.Run(() => ScrubAsync(instance, ssh, due, stop.Token)));
            }
        }
    }
    // Waits for the scrubs already started (tests and shutdown).
    public async Task DrainAsync() { Task[] tasks; lock (gate) tasks = running.ToArray(); await Task.WhenAll(tasks).ConfigureAwait(false); }

    private async Task ScrubAsync(string instance, ISshTransport ssh, VaultCleanup[] batch, CancellationToken token)
    {
        try
        {
            var report = await VaultCleaner.RunAsync(instance, ssh, batch, DecideFilesAsync, token).ConfigureAwait(false);
            Mutate(d => d.Cleanups.RemoveAll(c => batch.Any(b => ReferenceEquals(b, c))));
            await ReportAsync(instance, report, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            var retry = clock.UtcNow + RetryDelay;
            try { Mutate(d => { for (var i = 0; i < d.Cleanups.Count; i++) if (batch.Any(b => ReferenceEquals(b, d.Cleanups[i]))) d.Cleanups[i] = d.Cleanups[i] with { DueAt = retry }; }); }
            catch (Exception) { /* the next sweep retries from the saved state */ }
            Record(instance, $"Could not scrub {string.Join(", ", batch.Select(b => b.Name).Distinct())} from the VM; retrying in {RetryDelay.TotalMinutes:0} minutes.", true);
        }
        finally { lock (gate) cleaning.Remove(instance); wake.Writer.TryWrite(true); }
    }
    // File decisions, local scrubs and host scrubs alike, open one grid at a time.
    public async Task<IReadOnlyDictionary<string, string>?> DecideFilesAsync(FileDecisionPrompt prompt, CancellationToken token)
    {
        await decisions.WaitAsync(token).ConfigureAwait(false);
        try { return await prompts.DecideFilesAsync(prompt, token).ConfigureAwait(false); }
        finally { decisions.Release(); }
    }
    private async Task ReportAsync(string instance, VaultCleanReport report, CancellationToken token)
    {
        var names = string.Join(", ", report.Names);
        if (report.Unscannable.Count > 0) Record(instance, $"{string.Join(", ", report.Unscannable)} is too short to search for; not scrubbed.", true);
        if (report.AgentFiles > 0)
        {
            Record(instance, $"Redacted {names} from {Files(report.AgentFiles)} of agent logs.");
            await ToastAsync("Secret scrubbed", $"Redacted {names} from {Files(report.AgentFiles)} of agent logs on “{instance}”.", token).ConfigureAwait(false);
        }
        if (report.Redacted + report.Deleted + report.Kept > 0)
            Record(instance, $"Other files with {names}: {report.Redacted} redacted, {report.Deleted} deleted, {report.Kept} kept.", report.Kept > 0);
        if (report.Partial.Count > 0) Record(instance, $"Some data may stay in the write-ahead log of {string.Join(", ", report.Partial)} until that app checkpoints it.", true);
        if (report.Failures.Count > 0)
        {
            Record(instance, $"Could not clean {string.Join(", ", report.Failures)}.", true);
            await ToastAsync("Secret scrub incomplete", $"{Files(report.Failures.Count)} on “{instance}” still contain {names}. See the Key Vault window.", token, "warning").ConfigureAwait(false);
        }
        if (report.AgentFiles + report.Redacted + report.Deleted + report.Kept + report.Failures.Count == 0 && report.Unscannable.Count < report.Names.Count)
            Record(instance, $"Scanned the VM: no copies of {names} found.");
    }
    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    // ── helpers ─────────────────────────────────────────────────────────────────
    private VaultDocument Document => document ?? throw new VaultUnavailableException(unavailable ?? "The key vault is unavailable.");
    private VaultEntry? Find(string name) => Document.Secrets.FirstOrDefault(s => s.Name == name);
    private VaultLease? ActiveLease(string instance, string name, DateTimeOffset now) => Document.Leases.FirstOrDefault(l => l.Instance == instance && l.Name == name && Live(l, now));
    private static bool Live(VaultLease lease, DateTimeOffset now) => lease.ExpiresAt > now && lease.UsesLeft is null or > 0;
    private static string NewId() => Guid.NewGuid().ToString("N");
    // Ends a lease and queues a scrub of the value that VM saw (one per VM, secret and value). Callers end
    // leases before they replace or remove the entry, so its setting is the one of the value the VM saw;
    // an auto-approved value is never scrubbed.
    private void End(VaultDocument d, VaultLease lease, DateTimeOffset due)
    {
        d.Leases.Remove(lease);
        if (d.Secrets.FirstOrDefault(s => s.Name == lease.Name) is not { AutoApprove: false } entry) return;
        d.Cleanups.RemoveAll(c => c.Instance == lease.Instance && c.Name == lease.Name && c.Value.Reveal() == entry.Value.Reveal());
        d.Cleanups.Add(new(lease.Instance, lease.Name, entry.Value, entry.Username, due));
        wake.Writer.TryWrite(true);
    }
    private bool RemoveSecret(VaultDocument d, string name, VaultTombstone? tombstone = null)
    {
        if (d.Secrets.FirstOrDefault(s => s.Name == name) is not { } entry) return false;
        foreach (var held in d.Leases.Where(l => l.Name == name).ToArray()) End(d, held, clock.UtcNow + ReleaseDelay);
        d.Secrets.RemoveAll(s => s.Name == name);
        Bury(d, name, tombstone?.UpdatedAt ?? Stamp(entry.UpdatedAt), tombstone?.UpdatedBy);
        return true;
    }
    private string Writer => "pc:" + Document.InstallId;
    // Millisecond time of a local write, always after the version it replaces (a host clock may run ahead).
    private DateTimeOffset Stamp(params DateTimeOffset?[] after)
    {
        var ms = VaultSync.Ms(clock.UtcNow);
        foreach (var at in after) if (at is { } t) ms = Math.Max(ms, VaultSync.Ms(t) + 1);
        return VaultSync.Time(ms);
    }
    private static DateTimeOffset? Buried(VaultDocument d, string name) => d.Tombstones.FirstOrDefault(t => t.Name == name)?.UpdatedAt;
    private void Bury(VaultDocument d, string name, DateTimeOffset at, string? by = null)
    { d.Tombstones.RemoveAll(t => t.Name == name); d.Tombstones.Add(new(name, at, by ?? Writer)); }
    private static bool Migrate(VaultDocument d)
    {
        var changed = false;
        if (d.InstallId.Length == 0) { d.InstallId = NewId(); changed = true; }
        for (var i = 0; i < d.Secrets.Count; i++)
            if (d.Secrets[i].UpdatedBy.Length == 0) { d.Secrets[i] = d.Secrets[i] with { UpdatedBy = "pc:" + d.InstallId }; changed = true; }
        return changed;
    }
    private void Mutate(Action<VaultDocument> change)
    {
        lock (gate) { change(Document); store.Save(Document); }
        Changed?.Invoke();
    }
    internal void Record(string instance, string text, bool warning = false, string host = "")
    {
        lock (gate) { activity.Add(new(clock.UtcNow, instance, text, warning, host)); if (activity.Count > ActivityLimit) activity.RemoveAt(0); }
        Changed?.Invoke();
    }
    internal async Task ToastAsync(string title, string body, CancellationToken token, string level = "info")
    {
        try
        {
            if (await toasts.GetAvailabilityAsync(token).ConfigureAwait(false) != ToastAvailability.Available) return;
            await toasts.RaiseAsync(NotificationProtocol.Toast(new JsonObject { ["level"] = level, ["title"] = title, ["body"] = body, ["source"] = "Key vault" }, "construct://vault"), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* a toast is a courtesy; the activity list has the record */ }
    }
    public static string Access(int? uses, DateTimeOffset expires, DateTimeOffset now)
    {
        var left = expires - now; var local = TimeZoneInfo.ConvertTime(expires, TimeZoneInfo.Local);
        var span = left >= TimeSpan.FromDays(1) ? "1 day" : left >= TimeSpan.FromHours(1)
            ? $"{(int)left.TotalHours} h{(left.Minutes > 0 ? $" {left.Minutes} min" : "")}" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";
        return (uses is { } u ? $"{u} use{(u == 1 ? "" : "s")}, " : "") + $"until {local.ToString("HH:mm", CultureInfo.InvariantCulture)} ({span})";
    }

    // Idempotent: the runtime service stops the loop, the container disposes the singleton.
    // The token source is never disposed because brokers may still link to it while they shut down.
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try { await DrainAsync().ConfigureAwait(false); } catch (Exception) { }
    }
}
