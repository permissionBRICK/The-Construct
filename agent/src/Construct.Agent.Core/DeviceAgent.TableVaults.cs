using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.State;
using Construct.Keys;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Vault;

namespace Construct.Agent;

/// <summary>
/// The vault of an engine outside the account's store (E12: a shared host while the store is this user's PC, or a host
/// of the old release), kept in that engine's table as the Companion kept it and synchronized here with the account's
/// <c>secret</c> records both ways, under the Companion's rule (docs/plans/key-vault-hosted.md, "Vault entries and
/// sync"): per name the larger <c>updatedAt</c> wins, on a tie the larger <c>updatedBy</c>, and tombstones take part.
/// This device's winners go to the engine (<c>PUT /vault/entries</c>, where a new value or a delete ends the leases on the
/// old one); the engine's winners (what its VMs added, changed or deleted) become records with that VM as provenance.
/// <para>
/// Payloads travel as they are: both sides seal with <c>K</c> in the one format of <see cref="VaultSeal"/>, under the
/// entry's name and stamp. A payload is opened here only to check it and to read the sealed auto-approve flag (the
/// engine's readable copy of it is taken from there, never from a record an engine could change); one that does not
/// open with <c>K</c> is neither taken nor sent. A tombstone older than <see cref="TombstoneAge"/> that the engine does
/// not know is not sent: the engine would not keep it.
/// </para>
/// <para>
/// A pass runs after every vault change on this device, when another participant writes a secret record, when such an
/// engine is added or connected or its event stream connects, and every <see cref="TableVaultInterval"/>. A failure is
/// that engine's alone and is tried again at the next pass; one that keeps failing shows on the engine's vault row
/// (<see cref="VaultModeAsync"/>).
/// </para>
/// </summary>
public sealed partial class DeviceAgent
{
    /// <summary>Tombstones are kept this long by an engine's table (VaultRules.TombstoneAge), so every side learns of a delete.</summary>
    public static readonly TimeSpan TombstoneAge = TimeSpan.FromDays(30);

    /// <summary>Failed passes in a row after which an engine's vault row shows the failure.</summary>
    public const int TableFailuresShown = 3;

    // A feed request waits this long for a change (the store answers within 30 s, the client gives up after 30 s).
    private static readonly TimeSpan FeedWait = TimeSpan.FromSeconds(25);

    private readonly SemaphoreSlim _tablePass = new(1, 1);
    private readonly object _tableGate = new();
    private readonly ConcurrentDictionary<string, (int Count, string Message)> _tableFailures = new(StringComparer.Ordinal);
    private TaskCompletionSource _tableWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? _tableDue;
    private Task? _tableLoop, _tableFeed;

    /// <summary>How often the table vaults are synchronized without any other cause, so what a VM added there reaches this device.</summary>
    public TimeSpan TableVaultInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a pass waits after its cause, so a burst of changes (the move's copy of the old vault) makes one pass.</summary>
    public TimeSpan TableVaultDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Starts the passes and the watch of the store's feed; the agent's stop ends them.</summary>
    public void StartTableVaultSync()
    {
        lock (_tableGate)
        {
            _tableDue ??= Time.GetUtcNow() + TableVaultDelay;
            _tableLoop ??= Task.Run(() => TableLoopAsync(_stopping.Token));
            _tableFeed ??= Task.Run(() => TableFeedAsync(_stopping.Token));
        }
    }

    /// <summary>A pass after <see cref="TableVaultDelay"/>, unless one is due sooner.</summary>
    private void RequestTableSync()
    {
        lock (_tableGate)
        {
            var at = Time.GetUtcNow() + TableVaultDelay;
            if (_tableDue is not { } due || at < due) _tableDue = at;
        }

        Interlocked.Exchange(ref _tableWake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private void OnEngineConnected(string engineId)
    {
        if (Find(engineId) is { } entry && KeepsTable(entry)) RequestTableSync();
    }

    /// <summary>Engines this device is signed in at that keep the account's vault in their table (not part of the account's store).</summary>
    private EngineEntry[] TableEngines()
    {
        if (!_state.Registered) return [];
        lock (_state) return _state.Engines.Where(KeepsTable).ToArray();
    }

    private bool KeepsTable(EngineEntry entry) => !TakesPart(entry) && (entry.UserToken is not null || entry.WindowsSignIn);

    /// <summary>Why an engine's table vault is not in sync, once it failed <see cref="TableFailuresShown"/> passes in a row; never a value.</summary>
    internal string? TableVaultProblem(string engineId) =>
        _tableFailures.TryGetValue(engineId, out var failed) && failed.Count >= TableFailuresShown
            ? "The key vault here is not in sync with your secrets. " + failed.Message
            : null;

    private async Task TableLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Task wake;
            TimeSpan wait;
            lock (_tableGate)
            {
                wake = _tableWake.Task;
                wait = (_tableDue ??= Time.GetUtcNow() + TableVaultInterval) - Time.GetUtcNow();
            }

            if (wait > TimeSpan.Zero)
            {
                await Task.WhenAny(Task.Delay(wait, Time, stop), wake).ConfigureAwait(false);
                continue;
            }

            lock (_tableGate) _tableDue = Time.GetUtcNow() + TableVaultInterval;
            try { await SyncTableVaultsAsync(stop).ConfigureAwait(false); }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                // The store or the keys were not reached: the next pass tries again.
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Follows the store's feed while an engine keeps a table vault: a <c>secret</c> record another participant wrote
    /// (another device, or an engine for its VMs) asks for a pass.
    /// </summary>
    private async Task TableFeedAsync(CancellationToken stop)
    {
        long? cursor = null;
        while (!stop.IsCancellationRequested)
        {
            Task wake;
            lock (_tableGate) wake = _tableWake.Task;
            try
            {
                if (TableEngines().Length == 0)
                {
                    cursor = null;
                    await Task.WhenAny(Task.Delay(TableVaultInterval, Time, stop), wake).ConfigureAwait(false);
                    continue;
                }

                using var http = StoreHttp();
                var store = new HttpStoreClient(http);
                if (cursor is not { } since)
                {
                    // From now on: what is there already, the first pass takes.
                    long latest = 0;
                    while ((await store.FeedAsync(latest, TimeSpan.Zero, stop).ConfigureAwait(false)) is { Records.Count: > 0 } page) latest = page.Cursor;
                    cursor = latest;
                    continue;
                }

                var changes = await store.FeedAsync(since, FeedWait, stop).ConfigureAwait(false);
                if (changes.Records.Count == 0) continue;
                cursor = changes.Cursor;
                if (changes.Records.Any(r => r.Kind == RecordKinds.Secret && r.Actor != Actor)) RequestTableSync();
            }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                // The store was not reached; the passes still run on their schedule.
                await Task.WhenAny(Task.Delay(TableVaultInterval, Time, stop), wake).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass over every engine that keeps the account's vault in its table: each engine's outcome by id (null: in sync,
    /// else why not). Engines are synchronized one after the other, each with the records as the ones before left them.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> SyncTableVaultsAsync(CancellationToken ct)
    {
        var outcomes = new Dictionary<string, string?>(StringComparer.Ordinal);
        await _tablePass.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var engines = TableEngines();
            if (engines.Length == 0) return outcomes;
            var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
            var records = new Dictionary<string, SecretRecord>(StringComparer.Ordinal);
            foreach (var record in await ReadSecretsAsync(ct).ConfigureAwait(false))
                if (!records.TryGetValue(record.Value.Name, out var seen) || record.Revision > seen.Revision) records[record.Value.Name] = record;

            var pulled = false;
            foreach (var entry in engines)
            {
                try
                {
                    pulled |= await SyncTableAsync(entry, keys, records, ct).ConfigureAwait(false);
                    _tableFailures.TryRemove(entry.Id, out _);
                    outcomes[entry.Id] = null;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var message = ex is AgentFailure failure ? failure.Message : "The host or the store did not answer.";
                    _tableFailures.AddOrUpdate(entry.Id, (1, message), (_, before) => (before.Count + 1, message));
                    outcomes[entry.Id] = message;
                }
            }

            // What one engine's VMs wrote reaches the engines synchronized before it with the next pass.
            if (pulled && engines.Length > 1) RequestTableSync();
            return outcomes;
        }
        finally
        {
            _tablePass.Release();
        }
    }

    /// <summary>One engine's table against the records; true when a record was written from it.</summary>
    private async Task<bool> SyncTableAsync(EngineEntry entry, AccountKeys keys, Dictionary<string, SecretRecord> records, CancellationToken ct)
    {
        // An engine that keeps the account's vault as records takes part in the store after all: there is no table.
        if (await ModeOrNullAsync(entry.Id, ct).ConfigureAwait(false) is { Records: true }) return false;
        var body = await EngineSendAsync(entry, HttpMethod.Get, "api/v1/vault/entries", null, ct).ConfigureAwait(false)
            ?? throw new AgentFailure(502, "engine-refused", "The host sent no vault.");
        var keyCheck = Str(body, "keyCheck");
        if (keyCheck.Length == 0)
        {
            // The vault was never set up there: it is set up locked, with the check of K; a device unlocks each VM.
            await EngineSendAsync(entry, HttpMethod.Put, "api/v1/vault/settings", new { mode = "locked", keyCheck = VaultSeal.KeyCheck(keys.Vault) }, ct)
                .ConfigureAwait(false);
        }
        else if (!OpensCheck(keys.Vault, keyCheck))
        {
            throw new AgentFailure(409, "other-vault-key", "It was set up with another vault key; nothing is exchanged with it.");
        }

        var theirs = new Dictionary<string, TableEntry>(StringComparer.Ordinal);
        foreach (var node in body["entries"] as JsonArray ?? [])
        {
            if (TableEntry.Parse(node) is { } parsed && (!theirs.TryGetValue(parsed.Name, out var seen) || Newer(parsed, seen))) theirs[parsed.Name] = parsed;
        }

        var cutoff = (Time.GetUtcNow() - TombstoneAge).ToUnixTimeMilliseconds();
        var push = new List<TableEntry>();
        var take = new List<TableEntry>();
        foreach (var name in records.Keys.Union(theirs.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var mine = records.TryGetValue(name, out var record) ? TableEntry.Of(record) : null;
            var other = theirs.GetValueOrDefault(name);
            if (mine is not null && (other is null || Newer(mine, other)))
            {
                // A delete goes where the secret lives, and anywhere while it is recent; both sides deleted is no change.
                if (!mine.Deleted || other is { Deleted: false } || other is null && mine.UpdatedAt >= cutoff) push.Add(mine);
            }
            else if (other is not null && (mine is null || Newer(other, mine)))
            {
                if (!other.Deleted || mine is { Deleted: false }) take.Add(other);
            }
        }

        var rejected = new List<string>();
        var pulled = false;
        foreach (var wanted in take)
        {
            switch (await TakeAsync(entry, keys, records, wanted, ct).ConfigureAwait(false))
            {
                case true: pulled = true; break;
                case null: rejected.Add(wanted.Name); break;
            }
        }

        var send = new List<object>();
        foreach (var mine in push)
        {
            if (mine.Deleted)
            {
                send.Add(new { name = mine.Name, description = "", hasUsername = false, updatedAt = mine.UpdatedAt, updatedBy = mine.UpdatedBy, deleted = true });
                continue;
            }

            // The engine's readable flag is the sealed one; a payload that does not open with K is not sent.
            if (Open(keys.Vault, mine) is not { } opened) continue;
            // The same secret under an older stamp (every record the move copied has a new one) stays: a new payload
            // would end every lease on it where the engine cannot compare values.
            if (theirs.GetValueOrDefault(mine.Name) is { Deleted: false } there && Open(keys.Vault, there) == opened &&
                there.AutoApprove == opened.AutoApprove && SameText(there.Description, mine.Description)) continue;
            send.Add(new
            {
                name = mine.Name, description = mine.Description, hasUsername = opened.Username.Length > 0, payload = mine.Payload,
                updatedAt = mine.UpdatedAt, updatedBy = mine.UpdatedBy, deleted = false, autoApprove = opened.AutoApprove,
            });
        }

        foreach (var chunk in send.Chunk(500))
            await EngineSendAsync(entry, HttpMethod.Put, "api/v1/vault/entries", new { entries = chunk }, ct).ConfigureAwait(false);

        if (rejected.Count > 0)
            throw new AgentFailure(409, "unreadable", $"Its version of {string.Join(", ", rejected)} does not open with this account's vault key; yours is kept.");
        return pulled;
    }

    /// <summary>
    /// Writes the engine's newer version of one secret as a record, on the base revision it reads now; true when written,
    /// false when the record changed meanwhile (the next pass merges again), null when the payload does not open with <c>K</c>.
    /// </summary>
    private async Task<bool?> TakeAsync(EngineEntry engine, AccountKeys keys, Dictionary<string, SecretRecord> records, TableEntry theirs, CancellationToken ct)
    {
        (string Username, string Secret, bool AutoApprove)? opened = null;
        if (!theirs.Deleted)
        {
            opened = Open(keys.Vault, theirs);
            if (opened is not { } value || Encoding.UTF8.GetByteCount(value.Secret) > MaxSecretBytes) return null;
        }

        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            var stored = await store.GetAsync(SecretRecords.IdOf(theirs.Name), ct).ConfigureAwait(false);
            var current = stored is null || stored.Epoch != keys.Epoch ? null : SecretRecords.Read(keys.Records, stored);
            if (current is not null && !Newer(theirs, TableEntry.Of(current))) return false;
            if (theirs.Deleted && current is not { Deleted: false }) return false;

            var provenance = ProvenanceOf(engine, theirs, current, opened, keys.Vault);
            var value = theirs.Deleted
                ? new SecretRecordValue(theirs.Name, "", false, false, null, theirs.UpdatedAt, theirs.UpdatedBy, provenance)
                : new SecretRecordValue(theirs.Name, Clean(theirs.Description), opened!.Value.Username.Length > 0, opened.Value.AutoApprove, theirs.Payload,
                    theirs.UpdatedAt, theirs.UpdatedBy, provenance);
            string[] origins = provenance.Vm is { } vm ? ["vm:" + vm, Actor] : [Actor];
            var write = SecretRecords.Write(keys.Records, keys.Epoch, Actor, value, stored?.Revision ?? 0, theirs.Deleted, origins, current?.Payload);
            switch (await store.WriteAsync(write, ct).ConfigureAwait(false))
            {
                case WriteOutcome.Accepted accepted:
                    if (SecretRecords.Read(keys.Records, accepted.Record) is { } read) records[theirs.Name] = read;
                    return true;
                case WriteOutcome.Refused refused:
                    throw new AgentFailure(502, refused.Code, "The store refused the secret.");
            }
        }

        return false;
    }

    /// <summary>
    /// Who caused the engine's version (9.1): a VM there (<c>updatedBy</c> <c>vm:&lt;name&gt;</c>) added the secret, turned
    /// its auto-approve off, or changed or deleted it on an approval; anything else came from a device (the Companion).
    /// </summary>
    private SecretProvenance ProvenanceOf(EngineEntry engine, TableEntry theirs, SecretRecord? current,
        (string Username, string Secret, bool AutoApprove)? opened, byte[] vaultKey)
    {
        if (!theirs.UpdatedBy.StartsWith("vm:", StringComparison.Ordinal) || theirs.UpdatedBy.Length == 3)
            return new SecretProvenance(SecretProvenance.ByDevice, Device: theirs.UpdatedBy);
        var vm = theirs.UpdatedBy[3..];
        var engineId = engine.EngineId ?? engine.Id;
        if (current is not { Deleted: false } before) return new SecretProvenance(SecretProvenance.VmAdded, vm, engineId);
        if (opened is { } now && Open(vaultKey, TableEntry.Of(before)) is { } was && was.AutoApprove && !now.AutoApprove &&
            was.Username == now.Username && was.Secret == now.Secret)
            return new SecretProvenance(SecretProvenance.VmAutoApproveOff, vm, engineId);
        return new SecretProvenance(SecretProvenance.VmApproved, vm, engineId);
    }

    // The larger stamp wins; on a tie the larger writer (ordinal), as on every side.
    private static bool Newer(TableEntry a, TableEntry b) =>
        a.UpdatedAt > b.UpdatedAt || a.UpdatedAt == b.UpdatedAt && string.CompareOrdinal(a.UpdatedBy, b.UpdatedBy) > 0;

    private static (string Username, string Secret, bool AutoApprove)? Open(byte[] vaultKey, TableEntry entry)
    {
        if (entry.Deleted || entry.Payload is null) return null;
        try { return VaultSeal.OpenEntry(vaultKey, entry.Name, entry.UpdatedAt, entry.Payload); }
        catch (CryptographicException) { return null; }
    }

    private static bool SameText(string a, string b) => Spaces().Replace(a, " ").Trim() == Spaces().Replace(b, " ").Trim();

    [System.Text.RegularExpressions.GeneratedRegex("\\s+")]
    private static partial System.Text.RegularExpressions.Regex Spaces();

    private static bool OpensCheck(byte[] vaultKey, string keyCheck)
    {
        try
        {
            var plain = VaultSeal.Open(vaultKey, keyCheck, VaultSeal.CheckAad);
            return CryptographicOperations.FixedTimeEquals(plain, Encoding.UTF8.GetBytes(VaultSeal.CheckText));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// One entry as an engine's table keeps it (and a record, to compare): never a value, only its sealed payload.
    /// <paramref name="AutoApprove"/> is the readable copy the engine decides by.
    /// </summary>
    private sealed record TableEntry(string Name, string Description, string? Payload, long UpdatedAt, string UpdatedBy, bool Deleted, bool AutoApprove = false)
    {
        public static TableEntry Of(SecretRecord record) =>
            new(record.Value.Name, record.Value.Description, record.Deleted ? null : record.Value.Payload, record.Value.UpdatedAt, record.Value.UpdatedBy, record.Deleted,
                !record.Deleted && record.Value.AutoApprove);

        /// <summary>An entry of <c>GET /vault/entries</c>, or null when it is not a valid one.</summary>
        public static TableEntry? Parse(JsonNode? node)
        {
            var name = Str(node, "name");
            var by = Str(node, "updatedBy");
            var at = Long(node, "updatedAt");
            if (!SecretName().IsMatch(name) || by.Length == 0 || at <= 0) return null;
            var deleted = True(node, "deleted");
            var payload = deleted ? null : Str(node, "payload");
            if (!deleted && payload!.Length == 0) return null;
            return new TableEntry(name, Str(node, "description"), payload, at, by, deleted, !deleted && True(node, "autoApprove"));
        }

        private static bool True(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;

        public override string ToString() => $"TableEntry {{ Name = {Name}, UpdatedAt = {UpdatedAt} }}";
    }
}
