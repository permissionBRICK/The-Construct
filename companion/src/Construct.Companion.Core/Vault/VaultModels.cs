using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

// One stored secret. Origin is "user" or "agent:<instance>" (registered from a VM by `construct secret add`).
// UpdatedAt (ms) and UpdatedBy ("pc:<install id>" or "vm:<vm>") decide every merge with a host (last writer wins).
public sealed record VaultEntry(string Name, string Description, string Username, Secret Value, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Origin, string UpdatedBy = "")
{
    public override string ToString() => $"VaultEntry {{ Name = {Name} }}";
}
// A deleted (or renamed-away) name, kept 30 days so a host that still has the entry cannot bring it back.
public sealed record VaultTombstone(string Name, DateTimeOffset UpdatedAt, string UpdatedBy);

// A VM's permission to read one secret. UsesLeft null = unlimited until ExpiresAt.
// Origin: "approved" (the user said yes), "once" (single get), "added" (the VM registered the secret itself).
public sealed record VaultLease(string Id, string Instance, string Name, int? UsesLeft, DateTimeOffset ExpiresAt, DateTimeOffset GrantedAt, string Reason, string Origin);

// A finished lease whose value must be scrubbed from that VM. The value travels with it so a secret
// deleted (or replaced) in the meantime can still be found and removed.
public sealed record VaultCleanup(string Instance, string Name, Secret Value, string Username, DateTimeOffset DueAt)
{
    public override string ToString() => $"VaultCleanup {{ Instance = {Instance}, Name = {Name} }}";
}

// What this PC knows about one host's vault. Mode is the host's ("available" or "locked"; "" = never synced),
// KeyCheck its key check, NeedsKey that it was set up with a vault key this PC does not have.
public sealed record VaultHostState(string Mode = "", long Revision = 0, DateTimeOffset? LastSyncAt = null, string LastError = "", bool NeedsKey = false, string? KeyCheck = null);

public sealed class VaultDocument
{
    public List<VaultEntry> Secrets { get; } = [];
    public List<VaultLease> Leases { get; } = [];
    public List<VaultCleanup> Cleanups { get; } = [];
    public List<VaultTombstone> Tombstones { get; } = [];
    public Dictionary<string, VaultHostState> Hosts { get; } = new(StringComparer.Ordinal);
    // The vault key K (32 bytes) that encrypts entry payloads on every host; null until first needed.
    public byte[]? Key { get; set; }
    public string InstallId { get; set; } = "";
}

// What the window shows; values stay behind VaultService.Reveal.
public sealed record VaultSecretView(string Name, string Description, string Username, string Origin, DateTimeOffset UpdatedAt, int ActiveLeases);
public sealed record VaultLeaseView(string Id, string Instance, string Name, int? UsesLeft, DateTimeOffset ExpiresAt, string Reason, string Origin);
// Host is empty for this PC's own events and the host name for events pulled from a host's vault.
public sealed record VaultActivity(DateTimeOffset At, string Instance, string Text, bool Warning = false, string Host = "");
public sealed record VaultSecretInput(string Name, string Description, string Username, Secret? Value);

public sealed class VaultUnavailableException(string message) : Exception(message);
