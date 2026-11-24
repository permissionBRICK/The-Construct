using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Agent.Http;
using Construct.Agent.State;
using Construct.Keys;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Vault;

namespace Construct.Agent;

/// <summary>
/// The vault's device side (plan 8.2, 8.4, 9.1, 10.3): secrets are <c>secret</c> records in the account's store,
/// which this device reads and writes itself, opening each record with <c>D</c> and each value with <c>K</c>. A
/// write carries the base revision it read; on a stale base it re-reads and applies its one change again (9.6).
/// Approvals, entry requests, leases and modes are the engines' and are reached through each engine's vault
/// routes. Values leave this class only through <see cref="RevealSecretAsync"/>; <c>K</c> never does.
/// </summary>
public sealed partial class DeviceAgent
{
    private const int MaxSecretBytes = 32768, MaxText = 300, WriteAttempts = 5;

    // ---- secrets ----

    /// <summary>Every live secret: names, descriptions and access, never a value.</summary>
    public async Task<IReadOnlyList<SecretView>> ListSecretsAsync(CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        var leases = await LeasesAsync(ct).ConfigureAwait(false);
        var views = new List<SecretView>();
        foreach (var secret in (await ReadSecretsAsync(ct).ConfigureAwait(false)).Where(s => !s.Deleted).OrderBy(s => s.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            var v = secret.Value;
            // A device trusts only the auto-approve flag sealed with K (an engine holds D and could change the other).
            bool? auto = null;
            try { auto = VaultSeal.OpenEntry(keys.Vault, v.Name, v.UpdatedAt, v.Payload!).AutoApprove; }
            catch (CryptographicException) { }
            views.Add(new SecretView(v.Name, v.Description, v.HasUsername, auto ?? false, auto is not null, v.UpdatedAt, v.UpdatedBy,
                Provenance(v.Provenance), leases.Where(l => l.Name == v.Name).ToArray()));
        }

        return views;
    }

    /// <summary>
    /// Adds a secret under a free name. The view comes from the record just written: a new secret has no leases, so no
    /// engine is asked (the move's copy of the old vault adds one secret after the other).
    /// </summary>
    public async Task<SecretView> AddSecretAsync(SecretInput input, CancellationToken ct)
    {
        ValidateName(input.Name, "name");
        if (string.IsNullOrWhiteSpace(input.Value)) throw new AgentFailure(400, "invalid-value", "A secret needs a value.");
        if (Encoding.UTF8.GetByteCount(input.Value) > MaxSecretBytes) throw new AgentFailure(400, "invalid-value", "A secret is at most 32 KiB.");
        var description = Clean(input.Description);
        if (description.Length == 0) throw new AgentFailure(400, "invalid-description", "Say what the secret is for.");
        var written = await ChangeSecretAsync(input.Name, current =>
        {
            if (current is { Deleted: false }) throw new AgentFailure(409, "exists", $"A secret named {input.Name} exists already.");
            return (Clean(input.Username), input.Value, input.AutoApprove, description, false);
        }, ct).ConfigureAwait(false);
        return Written(written);
    }

    /// <summary>
    /// Changes a secret's description, username or value; a field left null keeps what it was. The view comes from the
    /// record just written, without leases: the list (<see cref="ListSecretsAsync"/>) asks every engine for those.
    /// </summary>
    public async Task<SecretView> ChangeSecretAsync(string name, SecretChange change, CancellationToken ct)
    {
        ValidateName(name, "name");
        if (change.Value is { } value && (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > MaxSecretBytes))
            throw new AgentFailure(400, "invalid-value", "A secret's value is 1 byte to 32 KiB.");
        var written = await ChangeSecretAsync(name, current =>
        {
            var (username, secret, auto) = Opened(current, name);
            var description = change.Description is null ? current!.Value.Description : Clean(change.Description);
            return (change.Username is null ? username : Clean(change.Username), change.Value ?? secret, auto, description, false);
        }, ct).ConfigureAwait(false);
        return Written(written);
    }

    // A live record this device just wrote: its flag is the one it sealed.
    private static SecretView Written(SecretRecordValue v) =>
        new(v.Name, v.Description, v.HasUsername, v.AutoApprove, true, v.UpdatedAt, v.UpdatedBy, Provenance(v.Provenance), []);

    public async Task SetAutoApproveAsync(string name, bool on, CancellationToken ct)
    {
        ValidateName(name, "name");
        await ChangeSecretAsync(name, current =>
        {
            var (username, secret, _) = Opened(current, name);
            return (username, secret, on, current!.Value.Description, false);
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteSecretAsync(string name, CancellationToken ct)
    {
        ValidateName(name, "name");
        await ChangeSecretAsync(name, current =>
        {
            if (current is not { Deleted: false }) throw NoSecret(name);
            return ("", "", false, "", true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The one route that returns a value: the user asked to see it.</summary>
    public async Task<SecretValue> RevealSecretAsync(string name, CancellationToken ct)
    {
        ValidateName(name, "name");
        var current = await ReadSecretAsync(name, ct).ConfigureAwait(false);
        var (username, secret, _) = Opened(current, name);
        return new SecretValue(name, username, secret);
    }

    /// <summary>
    /// Every secret of the account, which is what a device being revoked could read (8.4): the user rotates them at
    /// their source.
    /// </summary>
    public async Task<IReadOnlyList<SecretSummary>> SecretsReadableByAsync(string deviceId, CancellationToken ct)
    {
        if (!(await DevicesAsync(ct).ConfigureAwait(false)).Any(d => d.Id == deviceId)) throw new AgentFailure(404, "no-device", "No such device.");
        return (await ReadSecretsAsync(ct).ConfigureAwait(false)).Where(s => !s.Deleted).OrderBy(s => s.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SecretSummary(s.Value.Name, s.Value.Description)).ToArray();
    }

    /// <summary>Every recorded write of every secret, newest first, with who caused it (9.1); never a value.</summary>
    public async Task<IReadOnlyList<SecretHistoryItem>> SecretHistoryAsync(CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        var items = new List<SecretHistoryItem>();
        using var http = StoreHttp();
        foreach (var id in (await ReadSecretsAsync(ct).ConfigureAwait(false)).Select(s => SecretRecords.IdOf(s.Value.Name)))
        {
            using var response = await http.GetAsync($"records/{Uri.EscapeDataString(id)}/history?limit=100", ct).ConfigureAwait(false);
            await EnsureStoreAsync(response, ct).ConfigureAwait(false);
            var versions = (await response.Content.ReadFromJsonAsync<List<StoredRecord>>(StoreJson.Options, ct).ConfigureAwait(false))!.OrderBy(r => r.Revision).ToList();
            var existed = false;
            foreach (var version in versions)
            {
                var read = version.Epoch == keys.Epoch ? SecretRecords.Read(keys.Records, version) : null;
                var action = version.Deleted ? "deleted" : existed ? "changed" : "added";
                existed = !version.Deleted;
                items.Add(new SecretHistoryItem(id[SecretRecords.Prefix.Length..], action, version.Revision, version.StoredAt, version.Actor,
                    read is null ? null : Provenance(read.Value.Provenance)));
            }
        }

        return items.OrderByDescending(i => i.Revision).ToArray();
    }

    /// <summary>
    /// Re-reads and applies one change until the store takes it on the base it read (9.6); the value written. Engines that
    /// keep the vault in their table get it at the next pass (<see cref="RequestTableSync"/>).
    /// </summary>
    private async Task<SecretRecordValue> ChangeSecretAsync(string name, Func<SecretRecord?, (string Username, string Secret, bool Auto, string Description, bool Delete)> change, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            var stored = await store.GetAsync(SecretRecords.IdOf(name), ct).ConfigureAwait(false);
            var current = stored is null || stored.Epoch != keys.Epoch ? null : SecretRecords.Read(keys.Records, stored);
            var (username, secret, auto, description, delete) = change(current);
            // A new stamp after every earlier one: the payload's AAD binds it, and the history reads in its order.
            var stamp = Math.Max(Time.GetUtcNow().ToUnixTimeMilliseconds(), (current?.Value.UpdatedAt ?? 0) + 1);
            var payload = delete ? null : VaultSeal.SealEntry(keys.Vault, name, stamp, username, secret, auto);
            var value = new SecretRecordValue(name, delete ? "" : description, !delete && username.Length > 0, !delete && auto, payload, stamp, Actor,
                new SecretProvenance(SecretProvenance.ByDevice, Device: Actor));
            var write = SecretRecords.Write(keys.Records, keys.Epoch, Actor, value, stored?.Revision ?? 0, delete, [Actor], current?.Payload);
            switch (await store.WriteAsync(write, ct).ConfigureAwait(false))
            {
                case WriteOutcome.Accepted:
                    RequestTableSync();
                    return value;
                case WriteOutcome.Refused refused:
                    throw new AgentFailure(502, refused.Code, "The store refused the secret.");
            }
        }

        throw new AgentFailure(409, "write-conflict", $"The secret {name} kept changing; try again.");
    }

    private (string Username, string Secret, bool Auto) Opened(SecretRecord? current, string name)
    {
        if (current is not { Deleted: false }) throw NoSecret(name);
        try { return VaultSeal.OpenEntry(_keys!.Vault, name, current.Value.UpdatedAt, current.Value.Payload!); }
        catch (CryptographicException) { throw new AgentFailure(409, "unreadable", $"The secret {name} does not open with this account's vault key."); }
    }

    private async Task<SecretRecord?> ReadSecretAsync(string name, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var stored = await new HttpStoreClient(http).GetAsync(SecretRecords.IdOf(name), ct).ConfigureAwait(false);
        return stored is null || stored.Epoch != keys.Epoch ? null : SecretRecords.Read(keys.Records, stored);
    }

    /// <summary>Every <c>secret</c> record this device can open, tombstones included.</summary>
    private async Task<IReadOnlyList<SecretRecord>> ReadSecretsAsync(CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        var found = new List<SecretRecord>();
        long since = 0;
        while (true)
        {
            var page = await store.FeedAsync(since, TimeSpan.Zero, ct).ConfigureAwait(false);
            if (page.Records.Count == 0) break;
            foreach (var record in page.Records.Where(r => r.Kind == RecordKinds.Secret && r.Epoch == keys.Epoch))
            {
                if (SecretRecords.Read(keys.Records, record) is { } read) found.Add(read);
            }

            since = page.Cursor;
        }

        return found;
    }

    // ---- what the engines hold: leases, approvals, entry requests, modes ----

    /// <summary>The leases of every engine this device reaches; an engine that does not answer is left out.</summary>
    private async Task<IReadOnlyList<SecretLeaseView>> LeasesAsync(CancellationToken ct)
    {
        var leases = new List<SecretLeaseView>();
        foreach (var (engine, body) in await FromEnginesAsync("api/v1/vault/leases", ct).ConfigureAwait(false))
        foreach (var lease in body["leases"] as JsonArray ?? [])
        {
            leases.Add(new SecretLeaseView(engine.Id, engine.Name, Str(lease, "id"), Str(lease, "vm"), Str(lease, "name"),
                lease?["usesLeft"] is JsonValue uses && uses.TryGetValue<int>(out var left) ? left : null, Long(lease, "expiresAt")));
        }

        return leases;
    }

    /// <summary>The pending approvals of every engine, oldest first; engines that did not answer are named.</summary>
    public async Task<VaultApprovalsView> ApprovalsAsync(CancellationToken ct)
    {
        var approvals = new List<VaultApprovalItem>();
        var answered = await FromEnginesAsync("api/v1/vault/approvals", ct).ConfigureAwait(false);
        foreach (var (engine, body) in answered)
        foreach (var a in body["approvals"] as JsonArray ?? [])
        {
            var id = Str(a, "id");
            approvals.Add(new VaultApprovalItem(engine.Id, engine.Name, id, Str(a, "vm"), Str(a, "op"), Str(a, "title"), Str(a, "message"),
                Str(a, "action"), (a?["names"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "").ToArray(), Str(a, "reason"), Str(a, "source"),
                Long(a, "createdAt"), Long(a, "deadline"), Int(a, "uses"), Int(a, "ttlSeconds"),
                VaultDisplay.IsDisplayedElsewhere(VaultDisplayTracker.KindApproval, engine.Id, id)));
        }

        return new VaultApprovalsView(approvals.OrderBy(a => a.CreatedAt).ToArray(), Unanswered(answered));
    }

    /// <summary>Records which pending vault approvals and entry requests another app on this device shows inline.</summary>
    public void ReportDisplayedVaultItems(DisplayedVaultRequest? request) => VaultDisplay.Report(request);

    /// <summary>
    /// Allows or denies one approval. On an engine in <c>locked</c> mode an allow first unlocks the VM that asked, so
    /// the request it allows can be served: in <c>locked</c> mode a VM waits for a device (8.2). That holds for a vault in
    /// the engine's table too (an engine outside the account's store, E12), which the Companion unlocked the same way.
    /// </summary>
    /// <param name="ttlSeconds">With an allow of a lease request: the duration the user chose (1 minute to 24 hours); null keeps the VM's.</param>
    public async Task DecideApprovalAsync(string engineId, string id, bool allow, CancellationToken ct, int? ttlSeconds = null)
    {
        if (ttlSeconds is { } chosen && (!allow || chosen < 60 || chosen > 86400))
            throw new AgentFailure(400, "invalid-duration", "A duration goes with an allow and is 1 minute to 24 hours.");
        var entry = Find(engineId) ?? throw NoEngine();
        if (allow && await LockedModeAsync(engineId, ct).ConfigureAwait(false) is { } mode)
        {
            var pending = (await ApprovalsAsync(ct).ConfigureAwait(false)).Approvals.FirstOrDefault(a => a.Engine == engineId && a.Id == id)
                ?? throw new AgentFailure(404, "not-found", "No such request.");
            if (!mode.UnlockedVms.Contains(pending.Vm, StringComparer.OrdinalIgnoreCase)) await UnlockAsync(entry, pending.Vm, mode.Records, ct).ConfigureAwait(false);
        }

        await EngineSendAsync(entry, HttpMethod.Post, $"api/v1/vault/approvals/{Uri.EscapeDataString(id)}",
            ttlSeconds is null ? new { decision = allow ? "approve" : "deny" } : new { decision = "approve", ttlSeconds }, ct).ConfigureAwait(false);
        Answered("vault.approval.ended", engineId, id);
    }

    /// <summary>
    /// The files a scrub found still holding a secret after its lease ended, from every engine, oldest first; engines
    /// that did not answer are named. Agent logs are redacted without asking and are never listed; a file waits for the
    /// user's keep, redact or delete and never expires.
    /// </summary>
    public async Task<VaultFilesView> VaultFilesAsync(CancellationToken ct)
    {
        var files = new List<VaultFileItem>();
        var answered = await FromEnginesAsync("api/v1/vault/files", ct).ConfigureAwait(false);
        foreach (var (engine, body) in answered)
        foreach (var f in body["files"] as JsonArray ?? [])
        {
            files.Add(new VaultFileItem(engine.Id, engine.Name, Str(f, "id"), Str(f, "vm"), Str(f, "path"),
                (f?["names"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "").ToArray(), Str(f, "type"), Long(f, "size"), Long(f, "createdAt")));
        }

        return new VaultFilesView(files.OrderBy(f => f.CreatedAt).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray(), Unanswered(answered));
    }

    /// <summary>
    /// Keeps, redacts or deletes one file at the engine that found it. A redaction needs <c>K</c> on the VM: on an engine
    /// in <c>locked</c> mode it would wait for the VM's next unlock, so a redact first unlocks the VM, as an allow does.
    /// A VM that is not running cannot be unlocked; its redaction is recorded all the same and waits for that unlock.
    /// A delete needs no key.
    /// </summary>
    public async Task DecideFileAsync(string engineId, string id, string? action, CancellationToken ct)
    {
        if (action is not ("keep" or "redact" or "delete")) throw new AgentFailure(400, "invalid-action", "keep, redact or delete.");
        var entry = Find(engineId) ?? throw NoEngine();
        if (action == "redact" && await LockedModeAsync(engineId, ct).ConfigureAwait(false) is { } mode)
        {
            var files = (await EngineSendAsync(entry, HttpMethod.Get, "api/v1/vault/files", null, ct).ConfigureAwait(false))?["files"] as JsonArray ?? [];
            var vm = files.Where(f => Str(f, "id") == id).Select(f => Str(f, "vm")).FirstOrDefault()
                ?? throw new AgentFailure(404, "not-found", "No such file.");
            if (!mode.UnlockedVms.Contains(vm, StringComparer.OrdinalIgnoreCase))
            {
                try { await UnlockAsync(entry, vm, mode.Records, ct).ConfigureAwait(false); }
                catch (AgentFailure e) when (e.Code == "vm-not-running") { }
            }
        }

        await EngineSendAsync(entry, HttpMethod.Post, $"api/v1/vault/files/{Uri.EscapeDataString(id)}", new { action }, ct).ConfigureAwait(false);
        Answered("vault.file.ended", engineId, id);
    }

    /// <summary>
    /// The engine's vault mode for this account when it is <c>locked</c>: a VM there holds <c>K</c> only while a device has
    /// unlocked it, whether the engine keeps the vault as records or in its table. Null otherwise, also for a host of the
    /// old release, which has no <c>/vault/mode</c>.
    /// </summary>
    private async Task<VaultModeView?> LockedModeAsync(string engineId, CancellationToken ct) =>
        await ModeOrNullAsync(engineId, ct).ConfigureAwait(false) is { Mode: "locked" } mode ? mode : null;

    // The engine's vault mode; null for a host of the old release, which has no /vault/mode (and keeps the vault in its table).
    private async Task<VaultModeView?> ModeOrNullAsync(string engineId, CancellationToken ct)
    {
        try { return await ReadVaultModeAsync(engineId, ct).ConfigureAwait(false); }
        catch (AgentFailure e) when (e.Status == 404) { return null; }
    }

    /// <summary>
    /// A request answered here ends on every surface at once (finding 47), also where the engine sends no event of its
    /// own (a host on the old stack has no event stream).
    /// </summary>
    private void Answered(string type, string engineId, string id) => Events.Publish(type, engineId, new JsonObject { ["id"] = id });

    /// <summary>Ends a lease before its time (the engine scrubs the value from the VM as on expiry).</summary>
    public async Task EndLeaseAsync(string engineId, string id, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        await EngineSendAsync(entry, HttpMethod.Delete, $"api/v1/vault/leases/{Uri.EscapeDataString(id)}", null, ct).ConfigureAwait(false);
    }

    /// <summary>Requests from agents to enter a secret (10.3), from every engine, oldest first.</summary>
    public async Task<VaultEntryRequestsView> EntryRequestsAsync(CancellationToken ct)
    {
        var requests = new List<VaultEntryRequestItem>();
        var answered = await FromEnginesAsync("api/v1/vault/entry-requests", ct).ConfigureAwait(false);
        foreach (var (engine, body) in answered)
        foreach (var r in body["requests"] as JsonArray ?? [])
        {
            var id = Str(r, "id");
            requests.Add(new VaultEntryRequestItem(engine.Id, engine.Name, id, Str(r, "vm"), Str(r, "name"), Str(r, "description"),
                Str(r, "reason"), Str(r, "source"), Long(r, "createdAt"), Long(r, "expiresAt"),
                VaultDisplay.IsDisplayedElsewhere(VaultDisplayTracker.KindEntryRequest, engine.Id, id)));
        }

        return new VaultEntryRequestsView(requests.OrderBy(r => r.CreatedAt).ToArray(), Unanswered(answered));
    }

    /// <summary>
    /// Saves the value the user pasted for an entry request: sealed here with <c>K</c>, never auto-approved; the engine
    /// creates the secret only while its name is free (409 <c>exists</c> otherwise, nothing overwritten).
    /// </summary>
    public async Task EnterSecretAsync(string engineId, string id, EntryInput input, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        if (string.IsNullOrEmpty(input.Value) || Encoding.UTF8.GetByteCount(input.Value) > MaxSecretBytes)
            throw new AgentFailure(400, "invalid-value", "A secret's value is 1 byte to 32 KiB.");
        var request = (await EntryRequestsAsync(ct).ConfigureAwait(false)).Requests.FirstOrDefault(r => r.Engine == engineId && r.Id == id)
            ?? throw new AgentFailure(404, "not-found", "No such entry request.");
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        var existing = await ReadSecretAsync(request.Name, ct).ConfigureAwait(false);
        if (existing is { Deleted: false }) throw new AgentFailure(409, "exists", $"A secret named {request.Name} exists already; nothing was overwritten.");
        var username = Clean(input.Username);
        var stamp = Math.Max(Time.GetUtcNow().ToUnixTimeMilliseconds(), (existing?.Value.UpdatedAt ?? 0) + 1);
        var payload = VaultSeal.SealEntry(keys.Vault, request.Name, stamp, username, input.Value, autoApprove: false);
        await EngineSendAsync(entry, HttpMethod.Post, $"api/v1/vault/entry-requests/{Uri.EscapeDataString(id)}",
            new { payload, updatedAt = stamp, hasUsername = username.Length > 0 }, ct).ConfigureAwait(false);
        Answered("vault.entry-request.ended", engineId, id);
    }

    public async Task DeclineEntryAsync(string engineId, string id, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        await EngineSendAsync(entry, HttpMethod.Post, $"api/v1/vault/entry-requests/{Uri.EscapeDataString(id)}/decline", null, ct).ConfigureAwait(false);
        Answered("vault.entry-request.ended", engineId, id);
    }

    /// <summary>
    /// The engine's vault mode and whether it holds <c>K</c> now. A vault in the engine's table whose sync with this
    /// device's secrets keeps failing answers why instead (<c>vault-sync-failed</c>): the vault page shows it on that
    /// engine's row.
    /// </summary>
    public async Task<VaultModeView> VaultModeAsync(string engineId, CancellationToken ct)
    {
        if (TableVaultProblem(engineId) is { } problem) throw new AgentFailure(502, "vault-sync-failed", problem);
        return await ReadVaultModeAsync(engineId, ct).ConfigureAwait(false);
    }

    private async Task<VaultModeView> ReadVaultModeAsync(string engineId, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        var body = await EngineSendAsync(entry, HttpMethod.Get, "api/v1/vault/mode", null, ct).ConfigureAwait(false);
        return new VaultModeView(engineId, body?["mode"]?.GetValue<string>(), body?["holdsKey"]?.GetValue<bool>() ?? false,
            body?["records"]?.GetValue<bool>() ?? false, (body?["unlockedVms"] as JsonArray ?? []).Select(v => v?.GetValue<string>() ?? "").ToArray());
    }

    /// <summary>
    /// Switches an engine's vault mode: <c>available</c> gives it <c>K</c>, <c>locked</c> withdraws it (8.2). A vault kept as
    /// records gets <c>K</c> sealed to the engine's host key. A vault in the engine's table (an engine outside the account's
    /// store, E12, or a host of the old release) gets its settings as the Companion set them: the key itself for
    /// <c>available</c>, and the key's check, which the table keeps from its first setting on.
    /// </summary>
    public async Task<VaultModeView> SetVaultModeAsync(string engineId, string mode, CancellationToken ct)
    {
        if (mode is not ("available" or "locked")) throw new AgentFailure(400, "invalid-mode", "available or locked.");
        var entry = Find(engineId) ?? throw NoEngine();
        if (await ModeOrNullAsync(engineId, ct).ConfigureAwait(false) is { Records: true })
        {
            object body = mode == "available" ? new { mode, grant = await KeyGrantAsync(entry, ct).ConfigureAwait(false) } : new { mode };
            await EngineSendAsync(entry, HttpMethod.Put, "api/v1/vault/mode", body, ct).ConfigureAwait(false);
        }
        else
        {
            var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
            await EngineSendAsync(entry, HttpMethod.Put, "api/v1/vault/settings",
                new { mode, key = mode == "available" ? Convert.ToBase64String(keys.Vault) : null, keyCheck = VaultSeal.KeyCheck(keys.Vault) }, ct).ConfigureAwait(false);
            // A table set up only now gets the secrets.
            RequestTableSync();
        }

        return await ModeOrNullAsync(engineId, ct).ConfigureAwait(false) ?? new VaultModeView(engineId, mode, mode == "available", false, []);
    }

    /// <summary>Hands a running VM on a <c>locked</c> engine <c>K</c> for as long as it runs (<see cref="UnlockAsync"/>).</summary>
    public async Task UnlockVmAsync(string engineId, string vm, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        await UnlockAsync(entry, vm, await ModeOrNullAsync(engineId, ct).ConfigureAwait(false) is { Records: true }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unlocks a VM: where the engine keeps the vault as records, with <c>K</c> sealed to its host key; where it keeps it in its
    /// table, with the key itself, as the Companion unlocked each VM it started or connected to.
    /// </summary>
    private async Task UnlockAsync(EngineEntry entry, string vm, bool records, CancellationToken ct)
    {
        object body = records
            ? new { grant = await KeyGrantAsync(entry, ct).ConfigureAwait(false) }
            : new { key = Convert.ToBase64String((await OpenKeysAsync(ct).ConfigureAwait(false)).Vault) };
        await EngineSendAsync(entry, HttpMethod.Post, $"api/v1/vms/{Uri.EscapeDataString(vm)}/vault/unlock", body, ct).ConfigureAwait(false);
    }

    private async Task<object> KeyGrantAsync(EngineEntry entry, CancellationToken ct)
    {
        if (entry.EngineId is null || entry.HostPublicKey is null) throw new AgentFailure(409, "engine-not-connected", "Connect this engine first.");
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        var grant = EngineGrants.Grant(ParticipantPublicKey.Parse(entry.HostPublicKey), entry.EngineId, _state.Account!, EngineGrants.Vault, keys.Epoch, keys.Vault);
        return new { epoch = grant.Epoch, @sealed = grant.Sealed };
    }

    // ---- plumbing ----

    // All engines at once, so one that does not answer delays the others by nothing (each call has its own timeout).
    private async Task<IReadOnlyList<(EngineView Engine, JsonObject Body)>> FromEnginesAsync(string path, CancellationToken ct)
    {
        async Task<(EngineView Engine, JsonObject? Body)> Ask(EngineView engine)
        {
            try { return (engine, await EngineSendAsync(Find(engine.Id)!, HttpMethod.Get, path, null, ct).ConfigureAwait(false)); }
            catch (AgentFailure) { return (engine, null); } // Not reached or refused: the view names it.
        }

        var answers = await Task.WhenAll(Engines().Where(e => e.Enrolled || e.SignedIn).Select(Ask)).ConfigureAwait(false);
        return answers.Where(a => a.Body is not null).Select(a => (a.Engine, a.Body!)).ToArray();
    }

    private string[] Unanswered(IReadOnlyList<(EngineView Engine, JsonObject Body)> answered) =>
        Engines().Where(e => e.Enrolled || e.SignedIn).Select(e => e.Id).Except(answered.Select(a => a.Engine.Id)).ToArray();

    /// <summary>One call to an engine's API with this device's credential; a refusal keeps the engine's code, never its body.</summary>
    private async Task<JsonObject?> EngineSendAsync(EngineEntry entry, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var http = EngineClientFor(entry.Id) ?? throw NoEngine();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: AgentJsonWeb) };
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new AgentFailure(502, "engine-unreachable", ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AgentFailure(504, "engine-timeout", "The engine did not answer in time."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject;
            string code = "engine-refused", message = $"The engine answered {(int)response.StatusCode}.";
            try
            {
                if (JsonNode.Parse(text) is JsonObject problem)
                {
                    if (problem["code"]?.GetValue<string>() is { Length: > 0 } c) code = c;
                    if (problem["detail"]?.GetValue<string>() is { Length: > 0 } d) message = d;
                    else if (problem["title"]?.GetValue<string>() is { Length: > 0 } t) message = t;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

            var status = (int)response.StatusCode switch { 401 => 403, >= 400 and < 500 and var s => s, _ => 502 };
            throw new AgentFailure(status, code, message);
        }
    }

    private static readonly JsonSerializerOptions AgentJsonWeb = new(JsonSerializerDefaults.Web);

    private static string Str(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int? Int(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static long Long(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private static SecretProvenanceView Provenance(SecretProvenance p) => new(p.Kind, p.Vm, p.Engine, p.Device, p.RequestId);

    private static AgentFailure NoSecret(string name) => new(404, "no-secret", $"No secret named {name}.");

    private static void ValidateName(string? name, string field)
    {
        if (name is null || !SecretName().IsMatch(name))
            throw new AgentFailure(400, "invalid-" + field, "Secret names use letters, digits, '.', '_' and '-' (at most 64, starting with a letter or digit).");
    }

    private static string Clean(string? text)
    {
        var clean = Regex.Replace(text ?? "", "[\\u0000-\\u001F\\u007F]", " ").Trim();
        return clean.Length <= MaxText ? clean : clean[..MaxText];
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SecretName();
}

/// <summary>Who caused a write (9.1): <c>device</c>, <c>vm-added</c>, <c>vm-auto-approve-off</c>, <c>vm-approved</c> or <c>entered-on-request</c>.</summary>
public sealed record SecretProvenanceView(string Kind, string? Vm, string? Engine, string? Device, string? RequestId);

/// <param name="AutoApprove">The flag sealed with <c>K</c>; false when the value does not open (<paramref name="Readable"/> false).</param>
public sealed record SecretView(string Name, string Description, bool HasUsername, bool AutoApprove, bool Readable, long UpdatedAt, string UpdatedBy,
    SecretProvenanceView Provenance, IReadOnlyList<SecretLeaseView> Leases);

/// <param name="Id">The engine's lease id: <c>DELETE /api/vault/leases/{engine}/{id}</c> ends it.</param>
public sealed record SecretLeaseView(string Engine, string EngineName, string Id, string Vm, string Name, int? UsesLeft, long ExpiresAt);

public sealed record SecretSummary(string Name, string Description);

public sealed record SecretHistoryItem(string Name, string Action, long Revision, DateTimeOffset At, string Actor, SecretProvenanceView? Provenance);

public sealed record SecretInput(string Name, string Description, [property: ConstructSecret] string Value, string? Username = null, bool AutoApprove = false);

/// <summary>A null field keeps what the secret has.</summary>
public sealed record SecretChange(string? Description = null, string? Username = null, [property: ConstructSecret] string? Value = null);

public sealed record SecretValue(string Name, string Username, [property: ConstructSecret] string Value);

public sealed record AutoApproveRequest(bool On);

public sealed record EntryInput([property: ConstructSecret] string Value, string? Username = null);

/// <param name="Uses">The uses the VM asked for (null: any number until the lease ends).</param>
/// <param name="TtlSeconds">The lease duration the VM asked for (null: the engine's default, one hour); the user may choose another when allowing.</param>
/// <param name="DisplayedElsewhere">Another app on this device (such as T3 Code Desktop) recently reported showing this approval inline.</param>
public sealed record VaultApprovalItem(string Engine, string EngineName, string Id, string Vm, string Op, string Title, string Message, string Action,
    IReadOnlyList<string> Names, string Reason, string Source, long CreatedAt, long Deadline, int? Uses = null, int? TtlSeconds = null,
    bool DisplayedElsewhere = false);

/// <param name="Unreachable">Engines that did not answer: their requests are not in the list.</param>
public sealed record VaultApprovalsView(IReadOnlyList<VaultApprovalItem> Approvals, IReadOnlyList<string> Unreachable);

/// <param name="TtlSeconds">With <c>allow</c> on a lease request: the lease duration the user chose (60 to 86400 seconds).</param>
public sealed record ApprovalDecision(string Decision, int? TtlSeconds = null);

/// <param name="Description">Written by the agent that asked: shown as its plain text.</param>
/// <param name="DisplayedElsewhere">Another app on this device (such as T3 Code Desktop) recently reported showing this request inline.</param>
public sealed record VaultEntryRequestItem(string Engine, string EngineName, string Id, string Vm, string Name, string Description, string Reason,
    string Source, long CreatedAt, long ExpiresAt, bool DisplayedElsewhere = false);

public sealed record VaultEntryRequestsView(IReadOnlyList<VaultEntryRequestItem> Requests, IReadOnlyList<string> Unreachable);

/// <summary>A file on a VM that still holds secrets after their lease ended; never its content.</summary>
/// <param name="Names">The secrets found in it.</param>
/// <param name="Type"><c>text</c>, <c>sqlite</c> (a database; its side files go with it) or <c>binary</c>.</param>
/// <param name="Size">In bytes, as the scan found it; 0 when unknown.</param>
public sealed record VaultFileItem(string Engine, string EngineName, string Id, string Vm, string Path, IReadOnlyList<string> Names, string Type,
    long Size, long CreatedAt);

/// <param name="Unreachable">Engines that did not answer: their files are not in the list.</param>
public sealed record VaultFilesView(IReadOnlyList<VaultFileItem> Files, IReadOnlyList<string> Unreachable);

/// <param name="Action"><c>keep</c> leaves the file as it is, <c>redact</c> replaces the secrets in it, <c>delete</c> removes it.</param>
public sealed record FileDecision(string Action);

/// <param name="HoldsKey">The engine holds <c>K</c> now: granted, or in RAM for an unlocked VM.</param>
/// <param name="Records">The engine keeps this account's vault as records; false: a Companion vault.</param>
public sealed record VaultModeView(string Engine, string? Mode, bool HoldsKey, bool Records, IReadOnlyList<string> UnlockedVms);

public sealed record VaultModeRequest(string Mode);
