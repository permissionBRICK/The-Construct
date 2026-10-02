using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

// One stored secret. Origin is "user" or "agent:<instance>" (registered from a VM by `construct secret add`).
public sealed record VaultEntry(string Name, string Description, string Username, Secret Value, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Origin)
{
    public override string ToString() => $"VaultEntry {{ Name = {Name} }}";
}

// A VM's permission to read one secret. UsesLeft null = unlimited until ExpiresAt.
// Origin: "approved" (the user said yes), "once" (single get), "added" (the VM registered the secret itself).
public sealed record VaultLease(string Id, string Instance, string Name, int? UsesLeft, DateTimeOffset ExpiresAt, DateTimeOffset GrantedAt, string Reason, string Origin);

// A finished lease whose value must be scrubbed from that VM. The value travels with it so a secret
// deleted (or replaced) in the meantime can still be found and removed.
public sealed record VaultCleanup(string Instance, string Name, Secret Value, string Username, DateTimeOffset DueAt)
{
    public override string ToString() => $"VaultCleanup {{ Instance = {Instance}, Name = {Name} }}";
}

public sealed class VaultDocument
{
    public List<VaultEntry> Secrets { get; } = [];
    public List<VaultLease> Leases { get; } = [];
    public List<VaultCleanup> Cleanups { get; } = [];
}

// What the window shows; values stay behind VaultService.Reveal.
public sealed record VaultSecretView(string Name, string Description, string Username, string Origin, DateTimeOffset UpdatedAt, int ActiveLeases);
public sealed record VaultLeaseView(string Id, string Instance, string Name, int? UsesLeft, DateTimeOffset ExpiresAt, string Reason, string Origin);
public sealed record VaultActivity(DateTimeOffset At, string Instance, string Text, bool Warning = false);
public sealed record VaultSecretInput(string Name, string Description, string Username, Secret? Value);

public sealed class VaultUnavailableException(string message) : Exception(message);
