using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Notifications;
namespace Construct.Companion.Core.Vault;

// The key vault's single owner: answers VM requests, asks the user, counts lease uses, expires
// leases and scrubs every VM a lease ended on. All document changes run under gate and are saved
// before the lock is released; dialogs and SSH never run under it.
public sealed class VaultService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1), OnceTtl = TimeSpan.FromMinutes(10),
        ReleaseDelay = TimeSpan.FromSeconds(5), ExhaustedGrace = TimeSpan.FromMinutes(1), RetryDelay = TimeSpan.FromMinutes(5), Tick = TimeSpan.FromSeconds(10);
    private const int ActivityLimit = 100;
    private readonly VaultStore store;
    private readonly IPrompts prompts;
    private readonly IToastRaiser toasts;
    private readonly IClock clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim approvals = new(1), decisions = new(1);
    private readonly Dictionary<string, ISshTransport> attached = new(StringComparer.Ordinal);
    private readonly HashSet<string> cleaning = new(StringComparer.Ordinal);
    private readonly List<Task> running = [];
    private readonly List<VaultActivity> activity = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource stop = new();
    private VaultDocument? document;
    private string? unavailable;
    public event Action? Changed;

    public VaultService(VaultStore store, IPrompts prompts, IToastRaiser toasts, IClock clock)
    {
        this.store = store; this.prompts = prompts; this.toasts = toasts; this.clock = clock;
        try { document = store.Load(); }
        catch (VaultUnavailableException e) { unavailable = e.Message; }
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
                if (leasedOnly && lease is null) continue;
                items.Add(new JsonObject { ["name"] = entry.Name, ["description"] = entry.Description, ["hasUsername"] = entry.Username.Length > 0,
                    ["lease"] = lease is null ? null : VaultProtocol.Lease(lease) });
            }
        }
        var response = VaultProtocol.Response(request.Id, "ok", ""); response["items"] = items; return response;
    }

    private async Task<JsonObject> RequestAsync(string instance, VaultRequest request, CancellationToken token)
    {
        string[] missing; string[] described;
        lock (gate)
        {
            missing = request.Names.Where(n => Find(n) is null).ToArray();
            described = request.Names.Select(n => Find(n) is { } e ? Bullet(e) : "").ToArray();
        }
        if (missing.Length > 0) return VaultProtocol.Response(request.Id, "notFound", $"No secret named {string.Join(", ", missing)} in the key vault (see construct secret list).");
        // A uses-only lease still ends after a day, so a forgotten grant cannot outlive the task by weeks.
        var ttl = TimeSpan.FromSeconds(request.Ttl ?? (request.Uses is null ? DefaultTtl.TotalSeconds : VaultProtocol.MaxTtl));
        var message = $"The VM “{instance}” asks for access to:\n{string.Join('\n', described)}\n\nAccess: {Access(request.Uses, clock.UtcNow + ttl, clock.UtcNow)}"
            + Footer(request);
        var answer = await ApproveAsync(new("Key vault — access request", message), request.Deadline, token).ConfigureAwait(false);
        if (answer != Approval.Approved) return Refused(request, answer);
        // The lease runs from the approval, not from the request: a slow answer must not shorten it.
        var now = clock.UtcNow; var expires = now + ttl;
        VaultLease? granted = null; var names = new List<string>();
        Mutate(d =>
        {
            foreach (var name in request.Names.Where(n => Find(n) is not null))
            {
                d.Leases.RemoveAll(l => l.Instance == instance && l.Name == name);
                granted = new(NewId(), instance, name, request.Uses, expires, clock.UtcNow, request.Reason, "approved");
                d.Leases.Add(granted); names.Add(name);
            }
        });
        Record(instance, $"Access to {string.Join(", ", names)} approved ({Access(request.Uses, expires, now)}).");
        var response = VaultProtocol.Response(request.Id, "ok", "Approved."); response["names"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray());
        if (granted is not null) response["lease"] = VaultProtocol.Lease(granted);
        return response;
    }

    private async Task<JsonObject> GetAsync(string instance, VaultRequest request, CancellationToken token)
    {
        var name = request.Names[0]; string bullet; VaultLease? lease;
        lock (gate)
        {
            if (Find(name) is not { } entry) return NotFound(request, name);
            bullet = Bullet(entry); lease = ActiveLease(instance, name, clock.UtcNow);
        }
        if (lease is null)
        {
            var answer = await ApproveAsync(new("Key vault — one-time access", $"The VM “{instance}” asks to read this secret once:\n{bullet}" + Footer(request), "Allow once"),
                request.Deadline, token).ConfigureAwait(false);
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
        var response = VaultProtocol.Response(request.Id, "ok", "");
        response["secret"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(value.Value.Reveal()));
        response["username"] = value.Username; response["lease"] = VaultProtocol.Lease(after);
        return response;
    }

    private JsonObject Release(string instance, VaultRequest request)
    {
        var ended = new List<string>();
        Mutate(d =>
        {
            foreach (var lease in d.Leases.Where(l => l.Instance == instance && (request.All || request.Names.Contains(l.Name))).ToArray())
            { End(d, lease, clock.UtcNow + ReleaseDelay); if (!ended.Contains(lease.Name)) ended.Add(lease.Name); }
        });
        if (ended.Count > 0) Record(instance, $"Released {string.Join(", ", ended)}; scrubbing the VM.");
        var response = VaultProtocol.Response(request.Id, "ok", ended.Count == 0 ? "Nothing to release." : "Released.");
        response["names"] = new JsonArray(ended.Select(n => (JsonNode)n).ToArray());
        return response;
    }

    private async Task<JsonObject> AddAsync(string instance, VaultRequest request, CancellationToken token)
    {
        var name = request.Names[0]; string? bullet;
        lock (gate) bullet = Find(name) is { } existing ? Bullet(existing) : null;
        if (bullet is not null)
        {
            if (!request.Replace) return VaultProtocol.Response(request.Id, "exists", $"A secret named {name} already exists. Pick another name, or pass --replace (needs the user's approval).");
            var answer = await ApproveAsync(new("Key vault — replace secret", $"The VM “{instance}” wants to replace the value of:\n{bullet}\n\nNew description: {request.Description}" + Footer(request), "Replace"),
                request.Deadline, token).ConfigureAwait(false);
            if (answer != Approval.Approved) return Refused(request, answer);
        }
        var now = clock.UtcNow; var expires = now + TimeSpan.FromSeconds(request.Ttl ?? DefaultTtl.TotalSeconds);
        var lease = new VaultLease(NewId(), instance, name, null, expires, now, request.Reason, "added");
        bool replaced = false, taken = false;
        Mutate(d =>
        {
            var old = Find(name);
            // Another VM may have added the same name while this one was checked: no silent overwrite.
            if (old is not null && !request.Replace) { taken = true; return; }
            if (old is not null)
            {
                // Every VM that saw the old value gets scrubbed of it.
                foreach (var held in d.Leases.Where(l => l.Name == name).ToArray()) End(d, held, now + ReleaseDelay);
                d.Secrets.Remove(old); replaced = true;
            }
            d.Secrets.Add(new(name, request.Description, request.Username, request.Value!, old?.CreatedAt ?? now, now, "agent:" + instance));
            d.Leases.RemoveAll(l => l.Instance == instance && l.Name == name); d.Leases.Add(lease);
        });
        if (taken) return VaultProtocol.Response(request.Id, "exists", $"A secret named {name} already exists. Pick another name, or pass --replace (needs the user's approval).");
        Record(instance, $"{(replaced ? "Replaced" : "Stored")} {name} from the VM; it holds it {Access(null, expires, now)}.");
        await ToastAsync(replaced ? "Secret replaced" : "Secret stored", $"The VM “{instance}” {(replaced ? "replaced" : "stored")} “{name}” in the key vault.", token).ConfigureAwait(false);
        var response = VaultProtocol.Response(request.Id, "ok", "Stored."); response["names"] = new JsonArray(name); response["lease"] = VaultProtocol.Lease(lease);
        return response;
    }

    private async Task<JsonObject> DeleteAsync(string instance, VaultRequest request, CancellationToken token)
    {
        var name = request.Names[0]; string bullet;
        lock (gate) { if (Find(name) is not { } entry) return NotFound(request, name); bullet = Bullet(entry); }
        var answer = await ApproveAsync(new("Key vault — delete secret", $"The VM “{instance}” asks to delete this secret from the key vault:\n{bullet}" + Footer(request), "Delete"),
            request.Deadline, token).ConfigureAwait(false);
        if (answer != Approval.Approved) return Refused(request, answer);
        var deleted = false;
        Mutate(d => deleted = RemoveSecret(d, name));
        if (!deleted) return NotFound(request, name);
        Record(instance, $"Deleted {name} at the VM's request.");
        var response = VaultProtocol.Response(request.Id, "ok", "Deleted."); response["names"] = new JsonArray(name); return response;
    }

    private static JsonObject NotFound(VaultRequest request, string name) => VaultProtocol.Response(request.Id, "notFound", $"No secret named {name} in the key vault (see construct secret list).");
    private static JsonObject Refused(VaultRequest request, Approval answer) => VaultProtocol.Response(request.Id, "denied",
        answer == Approval.TimedOut ? "No answer from the user before the request timed out." : "The user denied the request.");
    private static string Bullet(VaultEntry entry) => $"  • {entry.Name}{(entry.Description.Length > 0 ? " — " + entry.Description : "")}{(entry.Username.Length > 0 ? " (with username)" : "")}";
    private static string Footer(VaultRequest request) => (request.Reason.Length > 0 ? $"\nReason given: “{request.Reason}”" : "") + (request.Source.Length > 0 ? $"\nRequested by {request.Source}" : "");

    private enum Approval { Approved, Denied, TimedOut }
    // One dialog at a time; the agent's deadline closes a dialog nobody answered.
    private async Task<Approval> ApproveAsync(ApprovalPrompt prompt, DateTimeOffset? deadline, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        var timer = deadline is { } due ? CancelAtAsync(due, linked) : Task.CompletedTask;
        try
        {
            await approvals.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return await prompts.ApproveAsync(prompt, linked.Token).ConfigureAwait(false) ? Approval.Approved : Approval.Denied; }
            finally { approvals.Release(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !stop.IsCancellationRequested) { return Approval.TimedOut; }
        finally { await linked.CancelAsync().ConfigureAwait(false); await timer.ConfigureAwait(false); }
    }
    private async Task CancelAtAsync(DateTimeOffset due, CancellationTokenSource source)
    {
        try
        {
            var wait = due - clock.UtcNow;
            if (wait > TimeSpan.Zero) await clock.DelayAsync(wait, source.Token).ConfigureAwait(false);
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    // ── the PC side: the Key Vault window ──────────────────────────────────────
    public IReadOnlyList<VaultSecretView> Secrets()
    {
        var now = clock.UtcNow;
        lock (gate) return document is null ? [] : document.Secrets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new VaultSecretView(s.Name, s.Description, s.Username, s.Origin, s.UpdatedAt, document.Leases.Count(l => l.Name == s.Name && Live(l, now)))).ToArray();
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
            var now = clock.UtcNow;
            if (old is not null)
            {
                if (old.Name != input.Name || input.Value is not null && input.Value.Reveal() != old.Value.Reveal())
                    foreach (var held in d.Leases.Where(l => l.Name == old.Name).ToArray()) End(d, held, now + ReleaseDelay);
                d.Secrets.Remove(old);
            }
            d.Secrets.Add(new(input.Name, description, username, input.Value ?? old!.Value, old?.CreatedAt ?? now, now, old?.Origin ?? "user"));
        });
    }
    public void Delete(string name)
    {
        var deleted = false; Mutate(d => deleted = RemoveSecret(d, name));
        if (deleted) Record("", $"Deleted {name}.");
    }
    public void Revoke(string leaseId)
    {
        VaultLease? ended = null;
        Mutate(d => { if (d.Leases.FirstOrDefault(l => l.Id == leaseId) is { } lease) { End(d, lease, clock.UtcNow); ended = lease; } });
        if (ended is not null) Record(ended.Instance, $"Revoked access to {ended.Name}; scrubbing the VM.");
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
            moved = store.QuarantineUnreadable(clock.UtcNow); document = new(); store.Save(document); unavailable = null;
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
    // Expires leases, then starts at most one scrub per attached VM for its due cleanups.
    public void Sweep()
    {
        var now = clock.UtcNow;
        bool expired; lock (gate) expired = document is not null && document.Leases.Any(l => l.ExpiresAt <= now);
        if (expired)
        {
            var names = new List<(string, string)>();
            Mutate(d => { foreach (var lease in d.Leases.Where(l => l.ExpiresAt <= now).ToArray()) { End(d, lease, now); names.Add((lease.Instance, lease.Name)); } });
            foreach (var (instance, name) in names) Record(instance, $"Access to {name} expired; scrubbing the VM.");
        }
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
            var report = await VaultCleaner.RunAsync(instance, ssh, batch, DecideAsync, token).ConfigureAwait(false);
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
    private async Task<IReadOnlyDictionary<string, string>?> DecideAsync(FileDecisionPrompt prompt, CancellationToken token)
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
    // Ends a lease and queues a scrub of the value that VM saw (one per VM, secret and value).
    private void End(VaultDocument d, VaultLease lease, DateTimeOffset due)
    {
        d.Leases.Remove(lease);
        if (d.Secrets.FirstOrDefault(s => s.Name == lease.Name) is not { } entry) return;
        d.Cleanups.RemoveAll(c => c.Instance == lease.Instance && c.Name == lease.Name && c.Value.Reveal() == entry.Value.Reveal());
        d.Cleanups.Add(new(lease.Instance, lease.Name, entry.Value, entry.Username, due));
        wake.Writer.TryWrite(true);
    }
    private bool RemoveSecret(VaultDocument d, string name)
    {
        if (d.Secrets.FirstOrDefault(s => s.Name == name) is null) return false;
        foreach (var held in d.Leases.Where(l => l.Name == name).ToArray()) End(d, held, clock.UtcNow + ReleaseDelay);
        d.Secrets.RemoveAll(s => s.Name == name); return true;
    }
    private void Mutate(Action<VaultDocument> change)
    {
        lock (gate) { change(Document); store.Save(Document); }
        Changed?.Invoke();
    }
    private void Record(string instance, string text, bool warning = false)
    {
        lock (gate) { activity.Add(new(clock.UtcNow, instance, text, warning)); if (activity.Count > ActivityLimit) activity.RemoveAt(0); }
        Changed?.Invoke();
    }
    private async Task ToastAsync(string title, string body, CancellationToken token, string level = "info")
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
