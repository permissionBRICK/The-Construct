using System.Collections.Concurrent;
using System.Security.Cryptography;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>A refusal the API answers as a coded problem document.</summary>
public sealed class VaultProblemException(int status, string code, string message, string? field = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Field { get; } = field;
}

/// <summary>What <c>GET /vault/entries</c> returns.</summary>
public sealed record VaultSyncView(long Revision, string? Mode, string? KeyCheck, IReadOnlyList<string> UnlockedVms,
    IReadOnlyList<VaultEntry> Entries, IReadOnlyList<VaultDevice> Devices);

/// <summary>
/// The hosted key vault's single owner (docs/plans/key-vault-hosted.md): answers guest requests, keeps the
/// pending approvals, counts lease uses, expires leases, schedules and classifies scrubs and holds the
/// unlocked vault keys. It mirrors the Companion's VaultService (same defaults, caps and texts), with
/// approvals as pending records answered by the user's Companion or a paired device.
///
/// Every durable change runs under <see cref="gate"/>; the pending approvals and the unlocked keys live
/// in memory only, so a service restart forgets them (an unanswered request then reads as unknown).
/// </summary>
public sealed partial class VaultHostService(IVaultStore store, IVmRepository vms, IHypervisorDriver driver,
    IVaultKeyProtector protector, IClock clock) : IVaultUnlocks
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1), OnceTtl = TimeSpan.FromMinutes(10),
        ReleaseDelay = TimeSpan.FromSeconds(5), ExhaustedGrace = TimeSpan.FromMinutes(1), RetryDelay = TimeSpan.FromMinutes(5),
        DefaultWait = TimeSpan.FromMinutes(10), MaxWait = TimeSpan.FromHours(24), PickupGrace = TimeSpan.FromMinutes(2),
        RedeliverAfter = TimeSpan.FromMinutes(30), Tick = TimeSpan.FromSeconds(10), StateCheck = TimeSpan.FromSeconds(30);
    public const int ActivityLimit = 100, MaxPendingPerVm = 10, MaxDevices = 50, MaxEntries = 1000, MaxPayloadChars = 90000;
    public const string LockedMessage = "The key vault is locked for this VM: start or connect it from your PC's Construct Companion.";
    public const string NotSetUpMessage = "The key vault is not set up on this host yet: open the Key Vault in your PC's Construct Companion.";

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Unlocked> unlocked = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Unlocked(string Owner, byte[] Key);

    // ── unlocked keys (locked mode) ─────────────────────────────────────────────

    /// <summary>Forgets (and zeroes) a VM's unlocked vault key. Idempotent.</summary>
    public void Drop(string vmName)
    {
        if (unlocked.TryRemove(vmName, out var entry)) CryptographicOperations.ZeroMemory(entry.Key);
    }

    public bool IsUnlocked(string vmName) => unlocked.ContainsKey(vmName);

    public IReadOnlyList<string> UnlockedVms(string owner) =>
        unlocked.Where(p => Ownership.SameName(p.Value.Owner, owner)).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// <c>POST /vms/{name}/vault/unlock</c>: keeps the user's <c>K</c> in RAM for this running primary VM.
    /// A no-op when the vault is always available, not set up yet (there is no key check to verify
    /// against; the Companion unlocks online VMs once it has set the vault up) or already unlocked.
    /// </summary>
    public async Task UnlockAsync(Vm vm, string? keyBase64, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary) throw new VaultProblemException(403, "vault-child", "Child VMs have no key vault access.");
        var state = await store.GetOwnerAsync(vm.Owner, ct).ConfigureAwait(false);
        if (state is not { Configured: true } || state.Mode == VaultModes.Available || unlocked.ContainsKey(vm.Name)) return;
        var key = VaultCrypto.DecodeKey(keyBase64) ?? throw Validation("key", "The vault key must be 32 bytes, base64-encoded.");
        try
        {
            if (vm.Deleting) throw new VaultProblemException(409, "vm-deleting", $"VM '{vm.Name}' is being deleted.");
            var observed = await driver.GetStateAsync(vm.Name, ct).ConfigureAwait(false);
            if (observed != VmState.Running)
                throw new VaultProblemException(409, "vm-not-running", $"VM '{vm.Name}' is {observed.ToString().ToLowerInvariant()}; only a running VM can be unlocked.");
            if (!VaultCrypto.Verify(key, state.KeyCheck!)) throw new VaultProblemException(400, "wrong-key", "That is not this vault's key.");
            var stored = key.ToArray();
            if (!unlocked.TryAdd(vm.Name, new(vm.Owner, stored))) { CryptographicOperations.ZeroMemory(stored); return; }
            await RecordAsync(vm.Owner, vm.Name, "Unlocked the key vault for this VM.", false, ct).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Drops the key of every unlocked VM that is gone, fenced or observed in any state but running.</summary>
    public async Task CheckUnlockedAsync(CancellationToken ct)
    {
        foreach (var name in unlocked.Keys.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var vm = await vms.GetAsync(name, ct).ConfigureAwait(false);
            if (vm is null || vm.Deleting || vm.Kind != VmKind.Primary) { Drop(name); continue; }
            VmState state;
            try { state = await driver.GetStateAsync(name, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { continue; /* not an observation; the next check retries */ }
            if (state != VmState.Running) Drop(name);
        }
    }

    // ── settings and sync ───────────────────────────────────────────────────────

    /// <summary>
    /// <c>PUT /vault/settings</c>. The first call sets the key check (later ones keep it); <c>available</c>
    /// wraps and stores the key, <c>locked</c> deletes the wrapped key. A key that does not open the key
    /// check is refused.
    /// </summary>
    public async Task PutSettingsAsync(string owner, string? mode, string? keyBase64, string? keyCheck, CancellationToken ct)
    {
        if (mode is not (null or VaultModes.Available or VaultModes.Locked)) throw Validation("mode", "Expected available or locked.");
        if (keyCheck is not null && !IsSealed(keyCheck, 1)) throw Validation("keyCheck", "The key check must be base64(nonce||tag||ciphertext).");
        byte[]? key = null;
        if (keyBase64 is not null && (key = VaultCrypto.DecodeKey(keyBase64)) is null) throw Validation("key", "The vault key must be 32 bytes, base64-encoded.");
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var state = await store.GetOwnerAsync(owner, ct).ConfigureAwait(false);
                // The first call sets the key check; later ones keep it (a re-sealed check of the same key
                // differs in its nonce), and every key given is verified against it.
                var check = state is { Configured: true } ? state.KeyCheck!
                    : keyCheck ?? throw Validation("keyCheck", "The first settings call must carry the key check.");
                if (key is not null && !VaultCrypto.Verify(key, check)) throw new VaultProblemException(400, "wrong-key", "That is not this vault's key.");
                var next = mode ?? state?.Mode ?? VaultModes.Available;
                string? wrapped = null;
                if (next == VaultModes.Available)
                {
                    if (key is not null) wrapped = protector.Wrap(key);
                    else if (state is { Configured: true, Mode: VaultModes.Available, WrappedKey: { } existing }) wrapped = existing;
                    else throw Validation("key", "Always-available mode needs the vault key.");
                }
                await store.SaveSettingsAsync(owner, next, check, wrapped, ct).ConfigureAwait(false);
                if (state?.Mode != next)
                    await RecordAsync(owner, "", next == VaultModes.Available ? "The vault on this host is always available." : "The vault on this host is locked; your Companion unlocks each VM it starts or connects to.", false, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<VaultSyncView> GetEntriesAsync(string owner, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await store.GetOwnerAsync(owner, ct).ConfigureAwait(false);
            var entries = await store.ListEntriesAsync(owner, ct).ConfigureAwait(false);
            var devices = await store.ListDevicesAsync(owner, ct).ConfigureAwait(false);
            return new(state?.Revision ?? 0, state?.Mode, state?.KeyCheck, UnlockedVms(owner),
                entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray(), devices);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// <c>PUT /vault/entries</c>: applies the merge rule per entry. A winner that carries a new value (or
    /// deletes the entry) ends every lease on the old value, so those VMs are scrubbed of it.
    /// </summary>
    public async Task<(long Revision, int Applied)> PutEntriesAsync(string owner, IReadOnlyList<VaultEntry> incoming, CancellationToken ct)
    {
        if (incoming.Count > MaxEntries) throw Validation("entries", $"At most {MaxEntries} entries per call.");
        var clean = incoming.Select((e, i) => CheckEntry(e, i)).ToArray();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;
            var state = await store.GetOwnerAsync(owner, ct).ConfigureAwait(false);
            var current = await store.ListEntriesAsync(owner, ct).ConfigureAwait(false);
            var winners = VaultRules.Merge(current, clean, now.ToUnixTimeMilliseconds());
            if (winners.Count == 0) return (state?.Revision ?? 0, 0);
            var key = KeyFor(state, null);
            try
            {
                foreach (var winner in winners)
                {
                    if (Live(current, winner.Name) is not { } old) continue;
                    if (!winner.Deleted && !ValueChanged(key, old, winner)) continue;
                    await EndLeasesAsync(owner, old.Name, Copy(old), now + ReleaseDelay, ct).ConfigureAwait(false);
                }
            }
            finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
            var revision = await store.SaveEntriesAsync(owner, winners, ct).ConfigureAwait(false);
            return (revision, winners.Count);
        }
        finally { gate.Release(); }
    }

    private static VaultEntry CheckEntry(VaultEntry entry, int index)
    {
        var field = $"entries[{index}]";
        if (!VaultProtocol.IsValidName(entry.Name)) throw Validation(field + ".name", "Secret names use letters, digits, '.', '_' and '-' (at most 64, starting with a letter or digit).");
        if (entry.UpdatedAt <= 0) throw Validation(field + ".updatedAt", "Expected Unix milliseconds.");
        var by = VaultProtocol.Sanitize(entry.UpdatedBy, 100);
        if (by.Length == 0) throw Validation(field + ".updatedBy", "Expected pc:<install id> or vm:<vm name>.");
        if (entry.Deleted) return new(entry.Name, "", false, null, entry.UpdatedAt, by, true);
        if (entry.Payload is not { } payload || payload.Length > MaxPayloadChars || !IsSealed(payload, 2))
            throw Validation(field + ".payload", "A live entry needs its sealed payload.");
        return new(entry.Name, VaultProtocol.Sanitize(entry.Description, VaultProtocol.MaxText), entry.HasUsername, payload, entry.UpdatedAt, by, false);
    }

    // Without K the host cannot tell a new value from a re-sealed one, so any new payload counts.
    private static bool ValueChanged(byte[]? key, VaultEntry old, VaultEntry winner)
    {
        if (old.Payload == winner.Payload) return false;
        if (key is null) return true;
        try
        {
            var before = VaultCrypto.OpenEntry(key, old.Name, old.UpdatedAt, old.Payload!).Secret;
            var after = VaultCrypto.OpenEntry(key, winner.Name, winner.UpdatedAt, winner.Payload!).Secret;
            return !string.Equals(before, after, StringComparison.Ordinal);
        }
        catch (CryptographicException) { return true; }
    }

    // ── leases (the Companion's Access tab) ─────────────────────────────────────

    public async Task<IReadOnlyList<VaultLease>> LeasesAsync(string owner, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return (await store.ListAsync<VaultLease>(owner, ct).ConfigureAwait(false)).Where(l => LiveLease(l, now)).OrderBy(l => l.ExpiresAt).ToArray();
    }

    public async Task RevokeLeaseAsync(string owner, string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var lease = (await store.ListAsync<VaultLease>(owner, ct).ConfigureAwait(false)).FirstOrDefault(l => l.Id == id)
                ?? throw new VaultProblemException(404, "not-found", "No such lease.");
            await EndAsync(lease, clock.UtcNow, null, ct).ConfigureAwait(false);
            await RecordAsync(owner, lease.Vm, $"Revoked access to {lease.Name}; scrubbing the VM.", false, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // ── paired devices ──────────────────────────────────────────────────────────

    public Task<IReadOnlyList<VaultDevice>> DevicesAsync(string owner, CancellationToken ct) => store.ListDevicesAsync(owner, ct);

    /// <summary>Pairs a device; the token is returned once and only its SHA-256 is stored.</summary>
    public async Task<(VaultDevice Device, string Token)> PairDeviceAsync(string owner, string? label, CancellationToken ct)
    {
        var clean = VaultProtocol.Sanitize(label, 60);
        if (clean.Length == 0) throw Validation("label", "Name the device.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if ((await store.ListDevicesAsync(owner, ct).ConfigureAwait(false)).Count >= MaxDevices)
                throw new VaultProblemException(409, "too-many-devices", $"At most {MaxDevices} paired devices; revoke one first.");
            var token = TokenHasher.GenerateSecret();
            var device = new VaultDevice(NewId(), owner, clean, TokenHasher.Hash(token), clock.UtcNow, null);
            await store.AddDeviceAsync(device, ct).ConfigureAwait(false);
            await RecordAsync(owner, "", $"Paired the device “{clean}” for approvals.", false, ct).ConfigureAwait(false);
            return (device, token);
        }
        finally { gate.Release(); }
    }

    public async Task RevokeDeviceAsync(string owner, string id, CancellationToken ct)
    {
        var device = (await store.ListDevicesAsync(owner, ct).ConfigureAwait(false)).FirstOrDefault(d => d.Id == id);
        if (device is null || !await store.DeleteDeviceAsync(owner, id, ct).ConfigureAwait(false))
            throw new VaultProblemException(404, "not-found", "No such device.");
        await RecordAsync(owner, "", $"Revoked the device “{device.Label}”.", false, ct).ConfigureAwait(false);
    }

    /// <summary>The device a token belongs to, or null. Records the use at most once a minute.</summary>
    public async Task<VaultDevice?> AuthenticateDeviceAsync(string token, CancellationToken ct)
    {
        var device = await store.FindDeviceAsync(TokenHasher.Hash(token), ct).ConfigureAwait(false);
        if (device is null) return null;
        var now = clock.UtcNow;
        if (device.LastUsedAt is not { } last || now - last >= TimeSpan.FromMinutes(1)) await store.TouchDeviceAsync(device.Id, now, ct).ConfigureAwait(false);
        return device;
    }

    public Task<IReadOnlyList<VaultEvent>> ActivityAsync(string owner, CancellationToken ct) => store.ListEventsAsync(owner, ct);

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A copy of <c>K</c> for this owner (and VM, in locked mode), or null. Callers zero it.</summary>
    private byte[]? KeyFor(VaultOwnerState? state, string? vm)
    {
        if (state is not { Configured: true }) return null;
        if (state.Mode == VaultModes.Available)
        {
            if (state.WrappedKey is not { } wrapped) return null;
            try
            {
                var key = protector.Unwrap(wrapped);
                if (key.Length == VaultCrypto.KeyBytes) return key;
                CryptographicOperations.ZeroMemory(key);
            }
            catch (CryptographicException) { }
            catch (FormatException) { }
            return null;
        }
        if (vm is not null) return unlocked.TryGetValue(vm, out var own) && Ownership.SameName(own.Owner, state.Owner) ? own.Key.ToArray() : null;
        foreach (var entry in unlocked.Values) if (Ownership.SameName(entry.Owner, state.Owner)) return entry.Key.ToArray();
        return null;
    }

    private bool HasKey(VaultOwnerState? state, string vm)
    {
        var key = KeyFor(state, vm);
        if (key is null) return false;
        CryptographicOperations.ZeroMemory(key);
        return true;
    }

    private static VaultEntry? Live(IEnumerable<VaultEntry> entries, string name) =>
        entries.FirstOrDefault(e => e.Name == name && !e.Deleted && e.Payload is not null);
    private static VaultSecretCopy Copy(VaultEntry entry) => new(entry.Name, entry.Payload!, entry.UpdatedAt);
    private static bool LiveLease(VaultLease lease, DateTimeOffset now) => lease.ExpiresAt > now && lease.UsesLeft is null or > 0;
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static VaultProblemException Validation(string field, string reason) => new(400, "validation", reason, field);
    private static VaultProblemException NotFound(string what) => new(404, "not-found", what);

    private static bool IsSealed(string value, int minPlain)
    {
        try { return Convert.FromBase64String(value).Length >= VaultCrypto.NonceBytes + VaultCrypto.TagBytes + minPlain; }
        catch (FormatException) { return false; }
    }

    // Ends a lease and queues a scrub of the value that VM saw (one per VM, secret and value).
    private async Task EndAsync(VaultLease lease, DateTimeOffset due, IReadOnlyList<VaultEntry>? entries, CancellationToken ct)
    {
        await store.DeleteAsync<VaultLease>(lease.Id, ct).ConfigureAwait(false);
        entries ??= await store.ListEntriesAsync(lease.Owner, ct).ConfigureAwait(false);
        if (Live(entries, lease.Name) is { } entry) await QueueCleanupAsync(lease.Owner, lease.Vm, Copy(entry), due, ct).ConfigureAwait(false);
    }

    // Every lease on a secret ends, and each VM is scrubbed of the given (old) value.
    private async Task EndLeasesAsync(string owner, string name, VaultSecretCopy copy, DateTimeOffset due, CancellationToken ct)
    {
        foreach (var lease in (await store.ListAsync<VaultLease>(owner, ct).ConfigureAwait(false)).Where(l => l.Name == name))
        {
            await store.DeleteAsync<VaultLease>(lease.Id, ct).ConfigureAwait(false);
            await QueueCleanupAsync(owner, lease.Vm, copy, due, ct).ConfigureAwait(false);
        }
    }

    private async Task QueueCleanupAsync(string owner, string vm, VaultSecretCopy copy, DateTimeOffset due, CancellationToken ct)
    {
        foreach (var same in (await store.ListAsync<VaultCleanup>(owner, ct).ConfigureAwait(false))
                     .Where(c => Ownership.SameName(c.Vm, vm) && c.Copy.Name == copy.Name && c.Copy.Payload == copy.Payload))
            await store.DeleteAsync<VaultCleanup>(same.Id, ct).ConfigureAwait(false);
        await store.SaveAsync(new VaultCleanup(NewId(), owner, vm, copy, due), ct).ConfigureAwait(false);
    }

    private Task RecordAsync(string owner, string vm, string text, bool warning, CancellationToken ct) =>
        store.AppendEventAsync(owner, new VaultEvent(clock.UtcNow, vm, text, warning), ActivityLimit, ct);
}
