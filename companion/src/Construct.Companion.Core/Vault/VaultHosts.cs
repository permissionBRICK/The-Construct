using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.Runtime.RuntimeJson;
namespace Construct.Companion.Core.Vault;

public sealed record VaultHostRef(string Slug, string Host, RemoteHostClient Client);
// A registered instance that lives on a host; VmName is its name in the host's /vms routes.
public sealed record VaultHostInstance(string Name, string Slug, string VmName);
public sealed record VaultHostView(string Slug, string Host, VaultHostState State, IReadOnlyList<VaultDevice> Devices, IReadOnlyList<string> Instances, int Online);
public sealed record VaultHostLeaseView(string Slug, string Host, VaultHostLease Lease);
// Url holds the device token and the T3 pairing token: show it once, never log it. Vm is empty for approvals only.
// Note is set when phones get the host service's self-signed address and says where to change that.
public sealed record VaultPairing(string Host, string Label, string Vm, Secret Url, string Note = "");

// The enrolled hosts and the hosted instances of this user, supplied by the Host layer.
public interface IVaultHostDirectory
{
    IReadOnlyList<VaultHostRef> Hosts();
    IReadOnlyList<VaultHostInstance> Instances(string slug);
    // Null when the stored credential is a known, enabled user of the host (whoami); otherwise why not.
    Task<string?> CheckUserAsync(string slug, CancellationToken cancellationToken);
    // T3 Code's pairing script on that instance, as Open T3 Code runs it. Stdout holds a single-use token.
    Task<ProcessResult> RunT3PairingAsync(string instance, CancellationToken cancellationToken);
}

// The network side of the key vault on hosted VMs (docs/plans/key-vault-hosted.md): syncs this PC's vault
// with every enrolled host, unlocks locked hosts for the user's VMs, lists host approvals with the local ones
// (VaultService's pending records) and brings scrub file decisions into the same grid as local ones. Values only travel inside K-encrypted payloads;
// K itself only in the unlock/settings bodies.
public sealed class VaultHosts : IAsyncDisposable
{
    public static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5), ChangeDelay = TimeSpan.FromSeconds(2), ApprovalInterval = TimeSpan.FromSeconds(3);
    // Files, activity and leases are polled every fifth approval round (15 s).
    public const int SlowRound = 5;
    public const string ApprovalsOnly = "*approvals-only*";
    private const string Unsupported = "This host's service has no key vault yet; update it on the host.";
    private const string KeyMissing = "This host's vault uses a key this PC does not have: import it (Import vault key…) from the PC that set it up.";
    private const string KeyElsewhere = "Waiting for the vault key: another host already uses one. Import it (Import vault key…) from the PC that set it up.";
    private readonly VaultService vault;
    private readonly IVaultHostDirectory directory;
    private readonly IPrompts prompts;
    private readonly IClock clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim passes = new(1);
    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);
    private readonly List<Task> running = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource stop = new();
    private DateTimeOffset? due;
    public event Action? Changed;

    public VaultHosts(VaultService vault, IVaultHostDirectory directory, IPrompts prompts, IClock clock)
    { this.vault = vault; this.directory = directory; this.prompts = prompts; this.clock = clock; }

    // What this process learned about a host (nothing of it is persisted).
    private sealed class Live
    {
        public readonly Dictionary<string, VaultHostInstance> Online = new(StringComparer.Ordinal);
        public readonly Dictionary<string, CancellationTokenSource> Open = new(StringComparer.Ordinal);
        public readonly HashSet<string> Answered = new(StringComparer.Ordinal), Files = new(StringComparer.Ordinal), Events = new(StringComparer.Ordinal);
        public bool Synced, Deciding, EventsSeen;
        public bool? Usable;
        public IReadOnlyList<VaultDevice> Devices = [];
        public IReadOnlyList<VaultHostLease> Leases = [];
    }
    private Live Of(string slug) { lock (gate) return live.TryGetValue(slug, out var l) ? l : live[slug] = new(); }
    private VaultHostRef? Host(string slug) => directory.Hosts().FirstOrDefault(h => h.Slug == slug);
    // A registry definition with a host service lives on that host and uses its vault; the SSH spool
    // broker serves only instances without one. A broken service URL still counts as hosted (slug "").
    public static VaultHostInstance? Instance(JsonObject definition)
    {
        if (StateJson.Text(definition["service"]?["url"]) is not { Length: > 0 } url) return null;
        var name = StateJson.String(definition["name"]);
        string slug; try { slug = RemoteHost.HostSlug(url); } catch (Exception e) when (e is ArgumentException or UriFormatException) { slug = ""; }
        return new(name, slug, StateJson.Text(definition["vmName"]) is { Length: > 0 } vm ? vm : name);
    }

    // ── scheduling ─────────────────────────────────────────────────────────────
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        lock (gate) due ??= clock.UtcNow + SyncInterval;
        vault.EntriesChanged += OnEntriesChanged;
        try { await Task.WhenAll(SyncLoopAsync(linked.Token), PollLoopAsync(linked.Token)).ConfigureAwait(false); }
        finally { vault.EntriesChanged -= OnEntriesChanged; }
    }
    // On demand (Sync now, a host seen for the first time): the next pass starts at once.
    public void RequestSync() => Schedule(TimeSpan.Zero);
    private void OnEntriesChanged() => Schedule(ChangeDelay);
    private void Schedule(TimeSpan delay)
    {
        lock (gate) { var at = clock.UtcNow + delay; due = due is { } current && current < at ? current : at; }
        wake.Writer.TryWrite(true);
    }
    private async Task SyncLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                DateTimeOffset next; lock (gate) next = due ??= clock.UtcNow + SyncInterval;
                var wait = next - clock.UtcNow;
                if (wait > TimeSpan.Zero) { await WaitAsync(wait, token).ConfigureAwait(false); continue; }
                lock (gate) due = clock.UtcNow + SyncInterval;
                try { await SyncAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { /* each host keeps its own error; the next pass retries */ }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task WaitAsync(TimeSpan wait, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delay = clock.DelayAsync(wait, linked.Token); var signal = wake.Reader.WaitToReadAsync(linked.Token).AsTask();
        await Task.WhenAny(delay, signal).ConfigureAwait(false); await linked.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(delay, signal).ConfigureAwait(false); } catch (OperationCanceledException) { }
        while (wake.Reader.TryRead(out _)) { }
        token.ThrowIfCancellationRequested();
    }
    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            for (var round = 0; !token.IsCancellationRequested; round++)
            {
                try { await PollAsync(round % SlowRound == 0, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { /* the next round retries */ }
                await clock.DelayAsync(ApprovalInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // ── sync ───────────────────────────────────────────────────────────────────
    // One pass over every enrolled host whose whoami is a known user.
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var again = false;
        await passes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (vault.Unavailable is not null) return;
            var fetched = new List<(VaultHostRef Host, VaultHostSnapshot Snapshot)>();
            foreach (var host in directory.Hosts())
            {
                try
                {
                    if (await directory.CheckUserAsync(host.Slug, cancellationToken).ConfigureAwait(false) is { } problem) { Fail(host, problem); continue; }
                    if (VaultSync.ParseEntries(await host.Client.VaultEntriesAsync(cancellationToken).ConfigureAwait(false)) is { } snapshot) fetched.Add((host, snapshot));
                    else Fail(host, Unsupported);
                }
                catch (RemoteApiException e) { Fail(host, e.Status == 404 ? Unsupported : e.Message); }
            }
            // K is born here, and only when no reachable host has one yet: a second PC imports it instead.
            if (!vault.HasKey && fetched.Count > 0 && fetched.All(f => f.Snapshot.KeyCheck is null)) vault.EnsureKey();
            foreach (var (host, snapshot) in fetched)
            {
                try { again |= await SyncHostAsync(host, snapshot, cancellationToken).ConfigureAwait(false); }
                catch (RemoteApiException e) { Fail(host, e.Message); }
            }
        }
        finally { passes.Release(); }
        // Entries pulled from one host reach the hosts synced before it on the next pass.
        if (again) Schedule(ChangeDelay);
        Changed?.Invoke();
    }
    // Approval polling is switched off by the approvals route itself (401/403/404), not by a failed pass.
    private void Fail(VaultHostRef host, string problem) => vault.UpdateHost(host.Slug, s => s with { LastError = problem });
    private async Task<bool> SyncHostAsync(VaultHostRef host, VaultHostSnapshot snapshot, CancellationToken token)
    {
        lock (gate) Of(host.Slug).Usable = true; // approvals work whatever the key situation
        var key = vault.KeyCopy();
        try
        {
            var state = vault.HostState(host.Slug);
            var check = snapshot.KeyCheck; var mode = snapshot.Mode;
            if (key is null || check is not null && !VaultCrypto.MatchesKeyCheck(key, check))
            {
                // Set up with a key this PC lacks: never push, the host would hold entries nobody else can open.
                vault.UpdateHost(host.Slug, s => s with { Mode = mode, Revision = snapshot.Revision, NeedsKey = check is not null, KeyCheck = check, LastError = check is null ? KeyElsewhere : KeyMissing });
                return false;
            }
            if (check is null)
            {
                // First contact: the host gets the key check, and K itself unless the user chose locked.
                mode = state.Mode == VaultSync.Locked ? VaultSync.Locked : VaultSync.Available;
                check = VaultCrypto.KeyCheck(key);
                await host.Client.PutVaultSettingsAsync(Settings(mode, key, check), token).ConfigureAwait(false);
            }
            var merge = vault.MergeFromHost(host.Host, snapshot.Entries, key);
            var revision = snapshot.Revision;
            if (merge.Push.Count > 0)
            {
                var reply = await host.Client.PutVaultEntriesAsync(new JsonObject { ["entries"] = new JsonArray(merge.Push.Select(e => (JsonNode)VaultSync.ToJson(e)).ToArray()) }, token).ConfigureAwait(false);
                if (reply?["revision"] is JsonValue r && r.TryGetValue<double>(out var next) && double.IsFinite(next)) revision = (long)next;
            }
            var error = merge.Rejected.Count > 0 ? $"{string.Join(", ", merge.Rejected)} on this host does not open with this PC's vault key." : "";
            vault.UpdateHost(host.Slug, _ => new(mode, revision, clock.UtcNow, error, false, check));
            VaultHostInstance[] locked;
            lock (gate) { var l = Of(host.Slug); l.Synced = true; locked = mode == VaultSync.Locked ? l.Online.Values.Where(i => !snapshot.UnlockedVms.Contains(i.VmName)).ToArray() : []; }
            // A locked host opens the vault for this user's VMs that are online now and not unlocked yet.
            foreach (var instance in locked) await UnlockAsync(instance, token).ConfigureAwait(false);
            await Quietly(PullActivityAsync(host, token)).ConfigureAwait(false);
            await Quietly(RefreshDevicesAsync(host, token)).ConfigureAwait(false);
            await Quietly(RefreshLeasesAsync(host, token)).ConfigureAwait(false);
            return merge.Pulled.Count > 0;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    private static JsonObject Settings(string mode, byte[] key, string check)
    {
        var body = new JsonObject { ["mode"] = mode };
        if (mode == VaultSync.Available) body["key"] = VaultCrypto.Export(key); // the host wraps it; locked hosts never get it
        body["keyCheck"] = check;
        return body;
    }

    // ── unlock ─────────────────────────────────────────────────────────────────
    // The runtime reports every online/offline transition of a hosted instance (on its probe thread:
    // the unlock itself runs in the background). A host not synced yet in this process is synced at once.
    public void Online(VaultHostInstance instance, bool online)
    {
        bool transition, synced;
        lock (gate)
        {
            var l = Of(instance.Slug);
            transition = online ? l.Online.TryAdd(instance.Name, instance) : l.Online.Remove(instance.Name);
            synced = l.Synced;
        }
        if (!online || !transition) return;
        if (vault.HostState(instance.Slug).Mode == VaultSync.Locked) Track(Task.Run(() => UnlockAsync(instance, stop.Token)));
        if (!synced) RequestSync();
    }
    // Right after the Companion itself started that VM (idempotent on the host).
    public Task AfterStartAsync(VaultHostInstance instance, CancellationToken cancellationToken) => UnlockAsync(instance, cancellationToken);
    // Failures only reach the activity list: the VM's vault stays locked until the next try.
    public async Task UnlockAsync(VaultHostInstance instance, CancellationToken cancellationToken)
    {
        if (Host(instance.Slug) is not { } host || vault.HostState(instance.Slug).NeedsKey || vault.KeyCopy() is not { } key) return;
        try { await host.Client.UnlockVaultAsync(instance.VmName, VaultCrypto.Export(key), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (RemoteApiException e) { vault.Record(instance.VmName, $"Could not unlock the key vault for this VM: {e.Message}", true, host.Host); }
        catch (Exception e) when (e is not OperationCanceledException) { vault.Record(instance.VmName, "Could not unlock the key vault for this VM.", true, host.Host); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // ── approvals, file decisions, activity ────────────────────────────────────
    // One round for every host with an online VM of this user: approvals, and with slow also files,
    // activity and leases.
    public async Task PollAsync(bool slow, CancellationToken cancellationToken)
    {
        foreach (var host in directory.Hosts())
        {
            lock (gate) { var l = Of(host.Slug); if (l.Online.Count == 0 || l.Usable == false) continue; }
            await Quietly(PollApprovalsAsync(host, cancellationToken)).ConfigureAwait(false);
            if (!slow) continue;
            await Quietly(PollFilesAsync(host, cancellationToken)).ConfigureAwait(false);
            await Quietly(PullActivityAsync(host, cancellationToken)).ConfigureAwait(false);
            await Quietly(RefreshLeasesAsync(host, cancellationToken)).ConfigureAwait(false);
        }
    }
    private static async Task Quietly(Task task) { try { await task.ConfigureAwait(false); } catch (RemoteApiException) { } }
    private async Task PollApprovalsAsync(VaultHostRef host, CancellationToken token)
    {
        IReadOnlyList<VaultHostApproval> approvals;
        try { approvals = VaultSync.ParseApprovals(await host.Client.VaultApprovalsAsync(token).ConfigureAwait(false)); }
        catch (RemoteApiException e) when (e.Status is 401 or 403 or 404) { lock (gate) Of(host.Slug).Usable = false; throw; }
        var current = approvals.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var vanished = new List<CancellationTokenSource>(); var fresh = new List<(VaultHostApproval, CancellationTokenSource)>();
        lock (gate)
        {
            var l = Of(host.Slug);
            foreach (var (id, wait) in l.Open.Where(o => !current.Contains(o.Key)).ToArray()) { l.Open.Remove(id); vanished.Add(wait); }
            l.Answered.IntersectWith(current);
            foreach (var approval in approvals.Where(a => !l.Open.ContainsKey(a.Id) && !l.Answered.Contains(a.Id)))
            { var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); l.Open[approval.Id] = wait; fresh.Add((approval, wait)); }
        }
        // Answered on a phone, or expired on the host: its pending record ends unanswered.
        foreach (var wait in vanished) try { await wait.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        foreach (var (approval, wait) in fresh) Track(AnswerAsync(host, approval, wait));
    }
    private async Task AnswerAsync(VaultHostRef host, VaultHostApproval approval, CancellationTokenSource wait)
    {
        try
        {
            string instance; lock (gate) instance = Of(host.Slug).Online.Values.FirstOrDefault(i => i.VmName == approval.Vm)?.Name ?? approval.Vm;
            var source = new VaultApprovalSource(instance, approval.Vm, approval.Op, approval.Names, null, host.Slug, host.Host, approval.Id, approval.CreatedAt);
            // Answered here (SendAsync has told the host), or its deadline passed (the host expires it as well).
            await vault.ApproveHostAsync(source, new(approval.Title, approval.Message, approval.Action), approval.Deadline,
                approved => SendAsync(host, approval, approved), wait.Token).ConfigureAwait(false);
            lock (gate) Of(host.Slug).Answered.Add(approval.Id);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate) { var l = Of(host.Slug); if (l.Open.TryGetValue(approval.Id, out var open) && ReferenceEquals(open, wait)) l.Open.Remove(approval.Id); }
            wait.Dispose();
        }
    }
    // 404 and 409 mean another device answered first, or the approval expired: not an error.
    private async Task<VaultDecision> SendAsync(VaultHostRef host, VaultHostApproval approval, bool approved)
    {
        try { await host.Client.AnswerVaultApprovalAsync(approval.Id, approved ? "approve" : "deny", stop.Token).ConfigureAwait(false); return VaultDecision.Decided; }
        catch (RemoteApiException e) when (e.Status == 404) { return VaultDecision.NotFound; }
        catch (RemoteApiException e) when (e.Status == 409) { return VaultDecision.AlreadyDecided; }
        catch (RemoteApiException e) { vault.Record(approval.Vm, $"Could not send your answer to the host: {e.Message}", true, host.Host); return VaultDecision.Failed; }
    }
    private async Task PollFilesAsync(VaultHostRef host, CancellationToken token)
    {
        var files = VaultSync.ParseFiles(await host.Client.VaultFilesAsync(token).ConfigureAwait(false));
        VaultHostFile[] fresh;
        lock (gate)
        {
            var l = Of(host.Slug);
            if (l.Deciding) return;
            l.Files.IntersectWith(files.Select(f => f.Id));
            fresh = files.Where(f => !l.Files.Contains(f.Id)).ToArray();
            if (fresh.Length == 0) return;
            l.Deciding = true; l.Files.UnionWith(fresh.Select(f => f.Id));
        }
        Track(DecideAsync(host, fresh));
    }
    // One grid per host batch; every file gets an answer, keep included.
    private async Task DecideAsync(VaultHostRef host, VaultHostFile[] files)
    {
        try
        {
            var vms = files.Select(f => f.Vm).Distinct(StringComparer.Ordinal).ToArray();
            var names = files.SelectMany(f => f.Names).Distinct(StringComparer.Ordinal).ToArray();
            var prompt = new FileDecisionPrompt(vms.Length == 1 ? $"Key vault — secrets left on “{vms[0]}”" : $"Key vault — secrets left on VMs on {host.Host}",
                $"These files on {(vms.Length == 1 ? $"the VM “{vms[0]}”" : $"your VMs on {host.Host}")} contain {string.Join(", ", names)}. Agent logs were cleaned already. Choose what to do with each file; keep leaves it untouched.",
                files.Select(f => new FileDecisionItem(f.Id, vms.Length == 1 ? f.Path : $"{f.Vm}: {f.Path}", VaultCleaner.Describe(f.Names, f.Type, f.Size))).ToArray());
            var choices = await vault.DecideFilesAsync(prompt, stop.Token).ConfigureAwait(false) ?? new Dictionary<string, string>();
            var failed = 0;
            foreach (var file in files)
            {
                var action = choices.TryGetValue(file.Id, out var chosen) && FileDecisionPrompt.Actions.Contains(chosen) ? chosen : FileDecisionPrompt.Keep;
                try { await host.Client.DecideVaultFileAsync(file.Id, action, stop.Token).ConfigureAwait(false); }
                catch (RemoteApiException e) when (e.Status is 404 or 409) { } // decided on a phone first
                catch (RemoteApiException) { failed++; lock (gate) Of(host.Slug).Files.Remove(file.Id); } // asked again on the next round
            }
            if (failed > 0) vault.Record(vms.Length == 1 ? vms[0] : "", $"Could not send {failed} file decision{(failed == 1 ? "" : "s")} to the host; asking again.", true, host.Host);
        }
        catch (OperationCanceledException) { }
        finally { lock (gate) Of(host.Slug).Deciding = false; }
    }
    // The host's events join the activity list. The first read only catches up; later scrub reports
    // raise the toasts a local scrub raises.
    private async Task PullActivityAsync(VaultHostRef host, CancellationToken token)
    {
        var events = VaultSync.ParseActivity(await host.Client.VaultActivityAsync(token).ConfigureAwait(false));
        List<VaultHostEvent> fresh; bool toast;
        lock (gate)
        {
            var l = Of(host.Slug); toast = l.EventsSeen; l.EventsSeen = true;
            static string Key(VaultHostEvent e) => $"{VaultSync.Ms(e.At)}|{e.Vm}|{e.Text}";
            fresh = events.Where(e => l.Events.Add(Key(e))).ToList();
            if (l.Events.Count > 1000) { l.Events.Clear(); l.Events.UnionWith(events.Select(Key)); }
        }
        vault.RecordHostEvents(host.Host, fresh);
        if (!toast) return;
        foreach (var e in fresh)
            if (!e.Warning && e.Text.StartsWith("Redacted ", StringComparison.Ordinal))
                await vault.ToastAsync("Secret scrubbed", $"{e.Text.TrimEnd('.')} on “{e.Vm}”.", token).ConfigureAwait(false);
            else if (e.Warning && e.Text.StartsWith("Could not clean ", StringComparison.Ordinal))
                await vault.ToastAsync("Secret scrub incomplete", $"Files on “{e.Vm}” still contain a secret. See the Key Vault window.", token, "warning").ConfigureAwait(false);
    }
    private async Task RefreshLeasesAsync(VaultHostRef host, CancellationToken token)
    {
        var leases = VaultSync.ParseLeases(await host.Client.VaultLeasesAsync(token).ConfigureAwait(false));
        lock (gate) { var l = Of(host.Slug); if (l.Leases.SequenceEqual(leases)) return; l.Leases = leases; }
        Changed?.Invoke();
    }
    private async Task RefreshDevicesAsync(VaultHostRef host, CancellationToken token)
    {
        var devices = VaultSync.ParseDevices(await host.Client.VaultDevicesAsync(token).ConfigureAwait(false));
        lock (gate) { var l = Of(host.Slug); if (l.Devices.SequenceEqual(devices)) return; l.Devices = devices; }
        Changed?.Invoke();
    }

    // ── the Key Vault window ───────────────────────────────────────────────────
    public IReadOnlyList<VaultHostView> Views()
    {
        var states = vault.HostStates();
        return directory.Hosts().Select(h =>
        {
            var instances = directory.Instances(h.Slug).Select(i => i.Name).ToArray();
            lock (gate) { var l = Of(h.Slug); return new VaultHostView(h.Slug, h.Host, states.GetValueOrDefault(h.Slug) ?? new(), l.Devices, instances, l.Online.Count); }
        }).ToArray();
    }
    public IReadOnlyList<VaultHostLeaseView> Leases()
    {
        var hosts = directory.Hosts();
        lock (gate) return hosts.SelectMany(h => Of(h.Slug).Leases.Select(l => new VaultHostLeaseView(h.Slug, h.Host, l))).OrderBy(l => l.Lease.ExpiresAt).ToArray();
    }
    // When the window opens: paired devices and host leases of every host that may have a vault.
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        foreach (var host in directory.Hosts())
        {
            lock (gate) if (Of(host.Slug).Usable == false) continue;
            await Quietly(RefreshDevicesAsync(host, cancellationToken)).ConfigureAwait(false);
            await Quietly(RefreshLeasesAsync(host, cancellationToken)).ConfigureAwait(false);
        }
    }
    // The window's actions answer null on success or the text to show.
    public async Task<string?> SetModeAsync(string slug, string mode, CancellationToken cancellationToken)
    {
        if (Host(slug) is not { } host) return "That host is no longer enrolled.";
        if (mode is not (VaultSync.Available or VaultSync.Locked)) return "Unknown mode.";
        if (!vault.HasKey) await SyncAsync(cancellationToken).ConfigureAwait(false);
        var state = vault.HostState(slug);
        if (state.NeedsKey) return KeyMissing;
        if (vault.KeyCopy() is not { } key) return state.LastError.Length > 0 ? state.LastError : "Sync with this host first.";
        try
        {
            var check = state.KeyCheck ?? VaultCrypto.KeyCheck(key);
            await host.Client.PutVaultSettingsAsync(Settings(mode, key, check), cancellationToken).ConfigureAwait(false);
            vault.UpdateHost(slug, s => s with { Mode = mode, KeyCheck = check, LastError = "" });
            vault.Record("", mode == VaultSync.Locked ? "The vault on this host now opens only for VMs your PC starts or connects to." : "The vault on this host is now always available.", host: host.Host);
            // Locking drops the host's copy of K: the user's running VMs keep access through an unlock.
            if (mode == VaultSync.Locked) { VaultHostInstance[] online; lock (gate) online = Of(slug).Online.Values.ToArray(); foreach (var i in online) await UnlockAsync(i, cancellationToken).ConfigureAwait(false); }
            return null;
        }
        catch (RemoteApiException e) { return e.Message; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public async Task<string?> RevokeLeaseAsync(string slug, string id, CancellationToken cancellationToken)
    {
        if (Host(slug) is not { } host) return "That host is no longer enrolled.";
        try { await host.Client.RevokeVaultLeaseAsync(id, cancellationToken).ConfigureAwait(false); }
        catch (RemoteApiException e) when (e.Status != 404) { return e.Message; }
        await Quietly(RefreshLeasesAsync(host, cancellationToken)).ConfigureAwait(false);
        return null;
    }
    public async Task<string?> RevokeDeviceAsync(string slug, string id, CancellationToken cancellationToken)
    {
        if (Host(slug) is not { } host) return "That host is no longer enrolled.";
        try { await host.Client.RevokeVaultDeviceAsync(id, cancellationToken).ConfigureAwait(false); }
        catch (RemoteApiException e) when (e.Status != 404) { return e.Message; }
        await Quietly(RefreshDevicesAsync(host, cancellationToken)).ConfigureAwait(false);
        return null;
    }
    // Asks which VM's T3 Code the phone logs in to (or approvals only) and a device name, mints the T3 link
    // first (so a failure leaves no orphan device), then pairs the device. Null = cancelled.
    public async Task<VaultPairing?> PairPhoneAsync(string slug, CancellationToken cancellationToken)
    {
        var host = Host(slug) ?? throw new InvalidOperationException("That host is no longer enrolled.");
        var instances = directory.Instances(slug);
        var picked = await prompts.PickAsync(new("Pair a phone", [.. instances.Select(i => new PickItem(i.Name, i.Name, $"Approvals, and T3 Code on “{i.VmName}”")),
            new PickItem(ApprovalsOnly, "Approvals only", "No T3 Code login")], Placeholder: $"The phone approves key vault requests of your VMs on {host.Host}. Which VM's T3 Code should it also log in to?"), cancellationToken).ConfigureAwait(false);
        if (picked?.FirstOrDefault() is not { } choice) return null;
        var label = await prompts.InputAsync(new("Pair a phone", "Name this device; the Hosts tab lists it under paired devices.", "Phone"), cancellationToken).ConfigureAwait(false);
        if (label is null) return null;
        label = Sanitize(label, 60); if (label.Length == 0) label = "Phone";
        string? next = null; var vm = "";
        if (choice != ApprovalsOnly)
        {
            var instance = instances.FirstOrDefault(i => i.Name == choice) ?? throw new InvalidOperationException("That VM is no longer registered.");
            var run = await directory.RunT3PairingAsync(instance.Name, cancellationToken).ConfigureAwait(false);
            var (link, error) = VaultSync.PhoneT3Link(run.Code, run.Stdout);
            next = link ?? throw new InvalidOperationException(error);
            vm = instance.VmName;
        }
        var reply = await host.Client.PairVaultDeviceAsync(label, cancellationToken).ConfigureAwait(false);
        var token = reply.Str("token");
        if (!VaultSync.IsDeviceToken(token)) throw new InvalidOperationException("The host did not return a device token.");
        var (web, serviceDefault) = VaultSync.PairingWebBase(reply, host.Client.BaseUrl);
        // A T3 Code page on the approval page's origin could read the device token: never hand out such a code.
        if (next is not null && VaultSync.SameOrigin(next, web))
        {
            if (reply.Str("id") is { Length: > 0 } id) await Quietly(host.Client.RevokeVaultDeviceAsync(id, cancellationToken)).ConfigureAwait(false);
            throw new InvalidOperationException(VaultSync.SameOriginError);
        }
        await Quietly(RefreshDevicesAsync(host, cancellationToken)).ConfigureAwait(false);
        return new(host.Host, label, vm, new Secret(VaultSync.PairingUrl(web, token, next)), serviceDefault ? VaultSync.SelfSignedNote(web) : "");
    }
    public async Task<string?> ShowKeyAsync(CancellationToken cancellationToken)
    {
        if (vault.Unavailable is { } problem) return problem;
        if (!vault.HasKey)
        {
            if (vault.HostStates().Values.Any(s => s.KeyCheck is not null)) return "This PC has no vault key, but your hosts already use one: import it from the PC that set them up.";
            vault.EnsureKey();
        }
        await prompts.ShowSecretOnceAsync("Vault key", vault.ExportKey()!, "Import this key on your other PC (Key Vault → Hosts → Import vault key…). With it, that PC reads and writes the same secrets on your hosts, so keep it as private as the secrets themselves.",
            cancellationToken).ConfigureAwait(false);
        return null;
    }
    public async Task<string?> ImportKeyAsync(CancellationToken cancellationToken)
    {
        if (vault.Unavailable is { } problem) return problem;
        var text = await prompts.InputAsync(new("Import vault key", "Paste the vault key that “Show vault key…” shows on the PC that set up your hosts.", Password: true), cancellationToken).ConfigureAwait(false);
        return text is null ? null : await ImportKeyAsync(text, cancellationToken).ConfigureAwait(false);
    }
    // The key must open the key check of at least one host that has one (fetched fresh first).
    public async Task<string?> ImportKeyAsync(string text, CancellationToken cancellationToken)
    {
        if (vault.Unavailable is { } problem) return problem;
        if (VaultCrypto.Import(text) is not { } key) return "That is not a vault key (44 characters of base64).";
        try
        {
            await RefreshKeyChecksAsync(cancellationToken).ConfigureAwait(false);
            var checks = vault.HostStates().Values.Select(s => s.KeyCheck).OfType<string>().ToArray();
            if (checks.Length > 0 && !checks.Any(c => VaultCrypto.MatchesKeyCheck(key, c))) return "This key does not open the vault on any of your hosts.";
            if (vault.KeyCopy() is { } current)
            {
                var same = CryptographicOperations.FixedTimeEquals(current, key); CryptographicOperations.ZeroMemory(current);
                if (same) return null;
                if (!await prompts.ConfirmAsync(new ConfirmationPrompt("Replace vault key", "Replace this PC's vault key with the imported one? Hosts set up with the current key will need it imported again.", "Replace"),
                    cancellationToken).ConfigureAwait(false)) return null;
            }
            vault.ImportKey(key);
            RequestSync();
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private async Task RefreshKeyChecksAsync(CancellationToken token)
    {
        foreach (var host in directory.Hosts())
        {
            try
            {
                if (await directory.CheckUserAsync(host.Slug, token).ConfigureAwait(false) is not null) continue;
                if (VaultSync.ParseEntries(await host.Client.VaultEntriesAsync(token).ConfigureAwait(false)) is { } snapshot)
                    vault.UpdateHost(host.Slug, s => s with { Mode = snapshot.Mode, KeyCheck = snapshot.KeyCheck ?? s.KeyCheck });
            }
            catch (RemoteApiException) { }
        }
    }

    // ── lifetime ───────────────────────────────────────────────────────────────
    private void Track(Task task) { lock (gate) { running.RemoveAll(t => t.IsCompleted); running.Add(task); } }
    // Waits for the pending approvals, grids and unlocks already started (tests and shutdown).
    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] tasks; lock (gate) { running.RemoveAll(t => t.IsCompleted); tasks = running.ToArray(); }
            if (tasks.Length == 0) return;
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
    }
}
