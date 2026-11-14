namespace Constructd.Core.Domain;

/// <summary>
/// The key vault of hosted VMs (docs/plans/key-vault-hosted.md). One vault per user, managed in the
/// Companion and synchronized here; values stay encrypted with the user's vault key <c>K</c>.
/// </summary>
public static class VaultModes
{
    /// <summary>The host keeps <c>K</c> wrapped with its master key and opens the vault at any time.</summary>
    public const string Available = "available";

    /// <summary><c>K</c> lives only in host RAM, per VM, after the user's Companion unlocked that VM.</summary>
    public const string Locked = "locked";
}

/// <summary>
/// One user's vault on this host: the sync revision and the settings. <see cref="Mode"/> is null until
/// the Companion has set the vault up (<c>PUT /vault/settings</c>).
/// </summary>
/// <param name="Records">The vault lives in <c>secret</c> records (an account in the new stack, plan 9.1): the mode
/// then follows whether a device granted this engine <c>K</c>, and there is no key check or wrapped key.</param>
public sealed record VaultOwnerState(string Owner, long Revision, string? Mode, string? KeyCheck, string? WrappedKey, bool Records = false)
{
    public bool Configured => Records || (Mode is not null && KeyCheck is not null);
}

/// <summary>
/// Who caused a write of a record-backed entry (plan 9.1): <see cref="Kind"/> is one of the constants,
/// <see cref="Device"/> the approving or entering device, <see cref="RequestId"/> the request the write answers.
/// </summary>
public sealed record VaultProvenance(string Kind, string? Vm = null, string? Device = null, string? RequestId = null)
{
    public const string ByDevice = "device", VmAdded = "vm-added", VmAutoApproveOff = "vm-auto-approve-off",
        VmApproved = "vm-approved", EnteredOnRequest = "entered-on-request";
}

/// <summary>
/// One synchronized entry. <see cref="Payload"/> is <c>base64(nonce||tag||ciphertext)</c> of
/// <c>{"username","secret","autoApprove"}</c> under <c>K</c> with the AAD <c>entry:&lt;name&gt;:&lt;updatedAt&gt;</c>;
/// a tombstone (<see cref="Deleted"/>) has none. <see cref="UpdatedAt"/> is in Unix milliseconds.
/// <see cref="AutoApprove"/>: VMs read it without the user's approval and are never scrubbed of it. The host
/// decides by this plain copy (it may lack <c>K</c>); PCs trust only the sealed one. For a record-backed vault
/// <see cref="Revision"/> is the record's store revision (the base of the next write) and <see cref="Provenance"/>
/// who wrote it.
/// </summary>
public sealed record VaultEntry(string Name, string Description, bool HasUsername, string? Payload, long UpdatedAt, string UpdatedBy, bool Deleted,
    bool AutoApprove = false, long Revision = 0, VaultProvenance? Provenance = null)
{
    public override string ToString() => $"VaultEntry {{ Name = {Name}, UpdatedAt = {UpdatedAt} }}";
}

/// <summary>The per-VM records of a vault (leases, pending scrubs, scrub jobs, file decisions).</summary>
public interface IVaultRecord
{
    string Id { get; }
    string Owner { get; }
    string Vm { get; }
}

/// <summary>
/// A VM's permission to read one secret. <see cref="UsesLeft"/> null = unlimited until
/// <see cref="ExpiresAt"/>. Origin: <c>approved</c>, <c>once</c> (a single get) or <c>added</c>.
/// <see cref="Seen"/> is the value the lease was granted on: when a record-backed entry changes under it (a device
/// replaced or deleted it), the lease ends and that value is scrubbed.
/// </summary>
public sealed record VaultLease(string Id, string Owner, string Vm, string Name, int? UsesLeft, DateTimeOffset ExpiresAt,
    DateTimeOffset GrantedAt, string Reason, string Origin, VaultSecretCopy? Seen = null) : IVaultRecord;

/// <summary>
/// The encrypted value a scrub searches for. A scrub keeps its own copy, so deleting or replacing the
/// entry cannot lose what has to be found.
/// </summary>
public sealed record VaultSecretCopy(string Name, string Payload, long UpdatedAt)
{
    public override string ToString() => $"VaultSecretCopy {{ Name = {Name} }}";
}

/// <summary>A finished lease whose value must still be scrubbed from that VM, once due.</summary>
public sealed record VaultCleanup(string Id, string Owner, string Vm, VaultSecretCopy Copy, DateTimeOffset DueAt) : IVaultRecord;

/// <summary>A file an apply step works on. <see cref="Indexes"/> point into the job's items.</summary>
public sealed record VaultScrubFile(string Path, string PathB64, string Action, IReadOnlyList<int> Indexes, string Type);

/// <summary>
/// A scrub job for one VM. <c>scan</c> searches for <see cref="Items"/>; <c>apply</c> redacts or deletes
/// <see cref="Files"/>. Kind: <c>scan</c>, <c>agent</c> (agent logs, redacted without asking) or
/// <c>decision</c> (files the user chose to redact or delete). Patterns are computed when the job is
/// delivered, which needs <c>K</c>.
/// </summary>
public sealed record VaultScrubJob(string Id, string Owner, string Vm, string Step, string Kind, IReadOnlyList<VaultSecretCopy> Items,
    IReadOnlyList<VaultScrubFile> Files, string? Origin, DateTimeOffset CreatedAt, DateTimeOffset NotBefore, DateTimeOffset? DeliveredAt,
    IReadOnlyList<string> Unscannable) : IVaultRecord;

/// <summary>A file outside the agent logs that holds a secret; it waits for the user's keep/redact/delete.</summary>
public sealed record VaultFileDecision(string Id, string Owner, string Vm, string Path, string PathB64, IReadOnlyList<int> Indexes,
    string Type, long Size, IReadOnlyList<VaultSecretCopy> Items, string Origin, DateTimeOffset CreatedAt) : IVaultRecord
{
    public IReadOnlyList<string> Names => Indexes.Where(i => i >= 0 && i < Items.Count).Select(i => Items[i].Name).Distinct(StringComparer.Ordinal).ToArray();
}

/// <summary>A paired approval device (a phone). Only the SHA-256 of its token is stored.</summary>
public sealed record VaultDevice(string Id, string Owner, string Label, string TokenHash, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>One line of a vault's activity list.</summary>
public sealed record VaultEvent(DateTimeOffset At, string Vm, string Text, bool Warning = false);
