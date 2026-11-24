using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    // Matches VaultHostService.MaxPayloadChars so an oversized secret is skipped instead of rejecting a whole chunk.
    internal const int MaxTablePayloadChars = 90_000;
    private const int MaxTableChunkEntries = 500;
    private const int MaxTableChunkPayloadChars = 4_000_000;

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

    /// <summary>Immutable snapshot of a table-mode engine's binding so a mid-pass sign-in change cannot misdirect writes.</summary>
    private sealed record TableEngineBinding(string Id, string Url, string Pin, string? EngineId, string? DeviceToken, string? UserToken, bool WindowsSignIn)
    {
        public static TableEngineBinding Of(EngineEntry entry) =>
            new(entry.Id, entry.Url, entry.Pin, entry.EngineId, entry.DeviceToken, entry.UserToken, entry.WindowsSignIn);

        public bool Matches(EngineEntry entry) =>
            entry.Url == Url &&
            string.Equals(entry.Pin, Pin, StringComparison.OrdinalIgnoreCase) &&
            entry.DeviceToken == DeviceToken &&
            entry.UserToken == UserToken &&
            entry.WindowsSignIn == WindowsSignIn;
    }

    /// <summary>Engines this device is signed in at that keep the account's vault in their table (not part of the account's store).</summary>
    private TableEngineBinding[] TableEngines()
    {
        if (!_state.Registered) return [];
        lock (_state) return _state.Engines.Where(KeepsTable).Select(TableEngineBinding.Of).ToArray();
    }

    private bool KeepsTable(EngineEntry entry) => !TakesPart(entry) && (entry.UserToken is not null || entry.WindowsSignIn);

    /// <summary>Why an engine's table vault is not in sync, once it failed <see cref="TableFailuresShown"/> passes in a row; never a value.</summary>
    internal string? TableVaultProblem(string engineId)
    {
        if (Find(engineId) is not { } entry || !KeepsTable(entry))
        {
            _tableFailures.TryRemove(engineId, out _);
            return null;
        }

        return _tableFailures.TryGetValue(engineId, out var failed) && failed.Count >= TableFailuresShown
            ? "The key vault here is not in sync with your secrets. " + failed.Message
            : null;
    }

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
            AccountKeys keys;
            Dictionary<string, SecretRecord> records;
            try
            {
                keys = await OpenKeysAsync(ct).ConfigureAwait(false);
                records = new Dictionary<string, SecretRecord>(StringComparer.Ordinal);
                foreach (var record in await ReadSecretsAsync(ct).ConfigureAwait(false))
                    if (!records.TryGetValue(record.Value.Name, out var seen) || record.Revision > seen.Revision) records[record.Value.Name] = record;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                const string message = "The account's key store did not answer.";
                foreach (var entry in engines)
                {
                    _tableFailures.AddOrUpdate(entry.Id, (1, message), (_, before) => (before.Count + 1, message));
                    outcomes[entry.Id] = message;
                }

                return outcomes;
            }

            var pulled = false;
            var changed = false;
            foreach (var entry in engines)
            {
                try
                {
                    pulled |= await SyncTableAsync(entry, keys, records, ct).ConfigureAwait(false);
                    _tableFailures.TryRemove(entry.Id, out _);
                    outcomes[entry.Id] = null;
                }
                catch (AgentFailure ex) when (ex.Code == "engine-changed" && !ct.IsCancellationRequested)
                {
                    _tableFailures.TryRemove(entry.Id, out _);
                    outcomes[entry.Id] = null;
                    changed = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var message = TableFailureMessage(ex);
                    _tableFailures.AddOrUpdate(entry.Id, (1, message), (_, before) => (before.Count + 1, message));
                    outcomes[entry.Id] = message;
                }
            }

            // What one engine's VMs wrote reaches the engines synchronized before it with the next pass.
            if (changed || pulled && engines.Length > 1) RequestTableSync();
            return outcomes;
        }
        finally
        {
            _tablePass.Release();
        }
    }

    private static string TableFailureMessage(Exception ex) => ex is AgentFailure failure
        ? failure.Code switch
        {
            "other-vault-key" or "unreadable" or "too-large" => failure.Message,
            "engine-timeout" => "The host did not answer in time.",
            "engine-unreachable" => "The host could not be reached.",
            _ => "The host refused the vault sync.",
        }
        : "The host or the store did not answer.";

    /// <summary>One engine's table against the records; true when a record was written from it.</summary>
    private async Task<bool> SyncTableAsync(TableEngineBinding entry, AccountKeys keys, Dictionary<string, SecretRecord> records, CancellationToken ct)
    {
        // An engine that keeps the account's vault as records takes part in the store after all: there is no table.
        if (await ModeRecordsAsync(entry, ct).ConfigureAwait(false))
        {
            _tableFailures.TryRemove(entry.Id, out _);
            return false;
        }

        var body = await TableSendAsync(entry, HttpMethod.Get, "api/v1/vault/entries", null, ct).ConfigureAwait(false)
            ?? throw new AgentFailure(502, "engine-refused", "The host sent no vault.");
        var keyCheck = Str(body, "keyCheck");
        if (keyCheck.Length == 0)
        {
            // The vault was never set up there: set it up locked only if still unset (so a concurrent setup as available
            // stays intact), then re-read the resulting key check before exchanging any entries.
            await TableSendAsync(entry, HttpMethod.Put, "api/v1/vault/settings",
                    new { mode = "locked", keyCheck = VaultSeal.KeyCheck(keys.Vault), ifUnset = true }, ct)
                .ConfigureAwait(false);
            body = await TableSendAsync(entry, HttpMethod.Get, "api/v1/vault/entries", null, ct).ConfigureAwait(false)
                ?? throw new AgentFailure(502, "engine-refused", "The host sent no vault.");
            keyCheck = Str(body, "keyCheck");
        }

        if (keyCheck.Length == 0 || !OpensCheck(keys.Vault, keyCheck))
            throw new AgentFailure(409, "other-vault-key", "It was set up with another vault key; nothing is exchanged with it.");

        var hostMode = Str(body, "mode");
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
                // A delete goes where the secret lives, and anywhere while it is within retention (including advancing an older tombstone).
                if (!mine.Deleted || other is { Deleted: false } || mine.UpdatedAt >= cutoff) push.Add(mine);
            }
            else if (other is not null && (mine is null || Newer(other, mine)))
            {
                // Keep a newer tombstone within retention even when the account has no live record so another host's stale copy cannot resurrect it.
                if (!other.Deleted || mine is { Deleted: false } || other.UpdatedAt >= cutoff) take.Add(other);
            }
        }

        var rejected = new List<string>();
        var pulled = false;
        foreach (var wanted in take)
        {
            switch (await TakeAsync(entry, keys, records, wanted, cutoff, ct).ConfigureAwait(false))
            {
                case true: pulled = true; break;
                case null: rejected.Add(wanted.Name); break;
            }
        }

        var send = new List<(object Entry, int PayloadChars)>();
        var tooLarge = new List<string>();
        foreach (var mine in push)
        {
            if (mine.Deleted)
            {
                send.Add((new { name = mine.Name, description = "", hasUsername = false, updatedAt = mine.UpdatedAt, updatedBy = mine.UpdatedBy, deleted = true }, 0));
                continue;
            }

            // The engine's readable flag is the sealed one; a payload that does not open with K is not sent.
            if (Open(keys.Vault, mine) is not { } opened) continue;
            // On a locked host (which lacks K and cannot compare values), skip re-sending an unchanged secret under a newer
            // stamp so active leases are not ended; an available host compares decrypted values itself and advances its stamp.
            if (hostMode != "available" && theirs.GetValueOrDefault(mine.Name) is { Deleted: false } there && Open(keys.Vault, there) == opened &&
                there.AutoApprove == opened.AutoApprove && SameText(there.Description, mine.Description)) continue;
            if (mine.Payload is not { Length: <= MaxTablePayloadChars })
            {
                tooLarge.Add(mine.Name);
                continue;
            }

            send.Add((new
            {
                name = mine.Name, description = mine.Description, hasUsername = opened.Username.Length > 0, payload = mine.Payload,
                updatedAt = mine.UpdatedAt, updatedBy = mine.UpdatedBy, deleted = false, autoApprove = opened.AutoApprove,
            }, mine.Payload.Length));
        }

        foreach (var chunk in ChunkTableEntries(send))
            await TableSendAsync(entry, HttpMethod.Put, "api/v1/vault/entries", new { entries = chunk }, ct).ConfigureAwait(false);

        if (rejected.Count > 0)
            throw new AgentFailure(409, "unreadable", $"Its version of {string.Join(", ", rejected)} does not open with this account's vault key; yours is kept.");
        if (tooLarge.Count > 0)
            throw new AgentFailure(413, "too-large", $"The host cannot store {string.Join(", ", tooLarge)} because its sealed payload exceeds the host limit; other secrets stay in sync.");
        return pulled;
    }

    private static IEnumerable<List<object>> ChunkTableEntries(List<(object Entry, int PayloadChars)> send)
    {
        var batch = new List<object>();
        var chars = 0;
        foreach (var (entry, payloadChars) in send)
        {
            if (batch.Count > 0 && (batch.Count >= MaxTableChunkEntries || chars + payloadChars > MaxTableChunkPayloadChars))
            {
                yield return batch;
                batch = [];
                chars = 0;
            }

            batch.Add(entry);
            chars += payloadChars;
        }

        if (batch.Count > 0) yield return batch;
    }

    private void VerifyTableBinding(TableEngineBinding binding)
    {
        lock (_state)
        {
            var live = _state.Engines.FirstOrDefault(e => e.Id == binding.Id);
            if (live is null || !KeepsTable(live) || !binding.Matches(live))
                throw new AgentFailure(409, "engine-changed", "The engine's sign-in changed during sync.");
        }
    }

    private async Task<bool> ModeRecordsAsync(TableEngineBinding binding, CancellationToken ct)
    {
        try
        {
            var body = await TableSendAsync(binding, HttpMethod.Get, "api/v1/vault/mode", null, ct).ConfigureAwait(false);
            return body?["records"]?.GetValue<bool>() ?? false;
        }
        catch (AgentFailure ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Sends one table-sync call bound to <paramref name="binding"/>; aborts if the engine's credential or pin changed.</summary>
    private async Task<JsonObject?> TableSendAsync(TableEngineBinding binding, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        VerifyTableBinding(binding);
        var shared = _handlers.For(binding.Id, binding.Url, binding.Pin, binding.WindowsSignIn && binding.DeviceToken is null ? _options.WindowsSignIn : null);
        using var http = new HttpClient(new TableStampingHandler(this, binding) { InnerHandler = shared })
        {
            BaseAddress = new Uri(binding.Url),
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: AgentJsonWeb) };
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (AgentFailure) { throw; }
        catch (HttpRequestException) { throw new AgentFailure(502, "engine-unreachable", "The host could not be reached."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AgentFailure(504, "engine-timeout", "The host did not answer in time."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject;
            var code = "engine-refused";
            try
            {
                if (JsonNode.Parse(text) is JsonObject problem && problem["code"]?.GetValue<string>() is { Length: > 0 } c) code = c;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

            var status = (int)response.StatusCode switch { 401 => 403, >= 400 and < 500 and var s => s, _ => 502 };
            throw new AgentFailure(status, code, $"The engine answered {(int)response.StatusCode}.");
        }
    }

    private sealed class TableStampingHandler(DeviceAgent agent, TableEngineBinding binding) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            agent.VerifyTableBinding(binding);
            if (agent.Reveal(binding.UserToken) is { } user)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Writes the engine's newer version of one secret as a record, on the base revision it reads now; true when written,
    /// false when the record changed meanwhile (the next pass merges again), null when the payload does not open with <c>K</c>.
    /// </summary>
    private async Task<bool?> TakeAsync(TableEngineBinding engine, AccountKeys keys, Dictionary<string, SecretRecord> records, TableEntry theirs, long cutoff, CancellationToken ct)
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
            if (theirs.Deleted && current is not { Deleted: false } && theirs.UpdatedAt < cutoff) return false;

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
                case WriteOutcome.Refused refused when refused.Status == 404 && theirs.Deleted && stored is null:
                    // An older store that refuses tombstones for unseen names: keep the pass moving.
                    return false;
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
    private SecretProvenance ProvenanceOf(TableEngineBinding engine, TableEntry theirs, SecretRecord? current,
        (string Username, string Secret, bool AutoApprove)? opened, byte[] vaultKey)
    {
        if (!theirs.UpdatedBy.StartsWith("vm:", StringComparison.Ordinal) || theirs.UpdatedBy.Length == 3)
            return new SecretProvenance(SecretProvenance.ByDevice, Device: theirs.UpdatedBy);
        var vm = theirs.UpdatedBy[3..];
        var engineId = engine.EngineId ?? engine.Id;
        if (theirs.Deleted) return new SecretProvenance(SecretProvenance.VmApproved, vm, engineId);
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
