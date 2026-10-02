using System.Text;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

// Pushed: this PC's winners the host lacks or holds older. Pulled: names the host's newer version replaced
// or deleted here. Rejected: newer host entries whose payload does not open with K (tampered, renamed, other key).
public sealed record VaultMergeResult(IReadOnlyList<VaultWireEntry> Push, IReadOnlyList<string> Pulled, IReadOnlyList<string> Rejected);

// The document side of the host sync: the vault key K, per-host state, the merge and host activity.
public sealed partial class VaultService
{
    public bool HasKey { get { lock (gate) return document?.Key is not null; } }
    public string InstallId { get { lock (gate) return document?.InstallId ?? ""; } }
    // A copy for one operation; the caller clears it.
    public byte[]? KeyCopy() { lock (gate) return document?.Key?.ToArray(); }
    public Secret? ExportKey() { lock (gate) return document?.Key is { } key ? new(VaultCrypto.Export(key)) : null; }
    // K is created the first time a host needs one (VaultHosts decides when that is).
    public void EnsureKey() { var created = false; Mutate(d => { if (d.Key is null) { d.Key = VaultCrypto.NewKey(); created = true; } }); if (created) Record("", "Created this PC's vault key."); }
    // A key from another PC replaces this PC's; hosts whose key check it opens no longer need one.
    public void ImportKey(byte[] key)
    {
        if (key.Length != VaultCrypto.KeyBytes) throw new ArgumentException("A vault key has 32 bytes.");
        Mutate(d =>
        {
            d.Key = key.ToArray();
            foreach (var (slug, state) in d.Hosts.ToArray())
                if (state.KeyCheck is { } check)
                {
                    var needs = !VaultCrypto.MatchesKeyCheck(key, check);
                    d.Hosts[slug] = state with { NeedsKey = needs, LastError = needs ? state.LastError : "" };
                }
        });
        Record("", "Imported the vault key.");
    }

    public IReadOnlyDictionary<string, VaultHostState> HostStates() { lock (gate) return document is null ? new Dictionary<string, VaultHostState>() : new Dictionary<string, VaultHostState>(document.Hosts); }
    public VaultHostState HostState(string slug) { lock (gate) return document?.Hosts.GetValueOrDefault(slug) ?? new(); }
    public void UpdateHost(string slug, Func<VaultHostState, VaultHostState> change)
    {
        lock (gate)
        {
            var before = Document.Hosts.GetValueOrDefault(slug) ?? new();
            var after = change(before);
            if (after == before) return;
            Document.Hosts[slug] = after; store.Save(Document);
        }
        Changed?.Invoke();
    }

    // Applies the host's winners here and returns this PC's winners for the host, under the rule both sides
    // use: per name the larger updatedAt wins, on a tie the larger updatedBy; tombstones take part like entries.
    public VaultMergeResult MergeFromHost(string host, IReadOnlyList<VaultWireEntry> remote, byte[] key)
    {
        var push = new List<VaultWireEntry>(); var pulled = new List<string>(); var rejected = new List<string>();
        var changed = false;
        lock (gate)
        {
            var d = Document;
            var theirs = new Dictionary<string, VaultWireEntry>(StringComparer.Ordinal);
            foreach (var r in remote)
                if (!theirs.TryGetValue(r.Name, out var seen) || VaultSync.Newer(r.UpdatedAt, r.UpdatedBy, seen.UpdatedAt, seen.UpdatedBy)) theirs[r.Name] = r;
            foreach (var r in theirs.Values)
            {
                var entry = d.Secrets.FirstOrDefault(s => s.Name == r.Name); var tomb = d.Tombstones.FirstOrDefault(t => t.Name == r.Name);
                var mine = entry is not null ? (VaultSync.Ms(entry.UpdatedAt), entry.UpdatedBy) : tomb is not null ? (VaultSync.Ms(tomb.UpdatedAt), tomb.UpdatedBy) : ((long, string)?)null;
                if (mine is { } m && !VaultSync.Newer(r.UpdatedAt, r.UpdatedBy, m.Item1, m.Item2)) continue;
                if (r.Deleted)
                {
                    var stone = new VaultTombstone(r.Name, VaultSync.Time(r.UpdatedAt), r.UpdatedBy);
                    if (entry is not null) { RemoveSecret(d, r.Name, stone); pulled.Add(r.Name); }
                    else Bury(d, r.Name, stone.UpdatedAt, stone.UpdatedBy);
                    changed = true; continue;
                }
                if (VaultCrypto.OpenEntry(key, r.Name, r.UpdatedAt, r.Payload) is not { } opened || Encoding.UTF8.GetByteCount(opened.Value.Reveal()) > VaultProtocol.MaxSecretBytes)
                { rejected.Add(r.Name); continue; }
                if (entry is not null)
                {
                    // A new value ends every local lease on the old one, exactly like an edit in the window.
                    if (opened.Value.Reveal() != entry.Value.Reveal())
                        foreach (var held in d.Leases.Where(l => l.Name == entry.Name).ToArray()) End(d, held, clock.UtcNow + ReleaseDelay);
                    d.Secrets.Remove(entry);
                }
                d.Tombstones.RemoveAll(t => t.Name == r.Name);
                var origin = r.UpdatedBy.StartsWith("vm:", StringComparison.Ordinal) ? "agent:" + r.UpdatedBy[3..] : entry?.Origin ?? "user";
                d.Secrets.Add(new(r.Name, r.Description, Runtime.RuntimeJson.Sanitize(opened.Username, VaultProtocol.MaxText), opened.Value,
                    entry?.CreatedAt ?? VaultSync.Time(r.UpdatedAt), VaultSync.Time(r.UpdatedAt), origin, r.UpdatedBy));
                pulled.Add(r.Name); changed = true;
            }
            foreach (var entry in d.Secrets)
            {
                var at = VaultSync.Ms(entry.UpdatedAt);
                if (!theirs.TryGetValue(entry.Name, out var r) || VaultSync.Newer(at, entry.UpdatedBy, r.UpdatedAt, r.UpdatedBy))
                    push.Add(new(entry.Name, entry.Description, entry.Username.Length > 0, VaultCrypto.SealEntry(key, entry.Name, at, entry.Username, entry.Value), at, entry.UpdatedBy, false));
            }
            foreach (var tomb in d.Tombstones)
            {
                var at = VaultSync.Ms(tomb.UpdatedAt);
                if (!theirs.TryGetValue(tomb.Name, out var r) || VaultSync.Newer(at, tomb.UpdatedBy, r.UpdatedAt, r.UpdatedBy)) push.Add(new(tomb.Name, "", false, null, at, tomb.UpdatedBy, true));
            }
            if (changed) store.Save(d);
        }
        if (changed) Changed?.Invoke();
        if (pulled.Count > 0) Record("", $"Synchronized {string.Join(", ", pulled)} from the host.", host: host);
        if (rejected.Count > 0) Record("", $"{string.Join(", ", rejected)} on the host does not open with this PC's vault key; kept this PC's version.", true, host);
        return new(push, pulled, rejected);
    }

    // Events from a host's activity list join this PC's, in time order.
    public void RecordHostEvents(string host, IReadOnlyList<VaultHostEvent> events)
    {
        if (events.Count == 0) return;
        lock (gate)
        {
            var merged = activity.Concat(events.Select(e => new VaultActivity(e.At, e.Vm, e.Text, e.Warning, host))).OrderBy(a => a.At).ToList();
            activity.Clear(); activity.AddRange(merged.Skip(Math.Max(0, merged.Count - ActivityLimit)));
        }
        Changed?.Invoke();
    }
}
