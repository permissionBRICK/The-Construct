using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>Kind of principal a token resolves to.</summary>
public enum TokenKind
{
    /// <summary>An admin-issued user token: full API surface for that user's role.</summary>
    User,

    /// <summary>
    /// A VM-scoped token injected at provision time: may only manage its own VM's forwards and post
    /// its own activity heartbeat (plan §4.6).
    /// </summary>
    Vm,
}

/// <summary>The identity a token resolves to.</summary>
/// <param name="Name">User name, or <c>vm:&lt;vmName&gt;</c> for VM tokens.</param>
/// <param name="VmName">Set for <see cref="TokenKind.Vm"/> tokens only.</param>
public sealed record TokenPrincipal(TokenKind Kind, string Name, Role Role, string? VmName);

/// <summary>A freshly issued token. The plaintext is returned exactly once and never persisted.</summary>
public sealed record IssuedToken(ApiToken Token, string Plaintext);

/// <summary>
/// Issues and validates tokens. Only SHA-256 hashes are stored (plan §4.4); plaintext is never
/// written to the store or to a log.
/// </summary>
public interface ITokenService
{
    /// <summary>Issues a user token. Admin-only at the API layer.</summary>
    Task<IssuedToken> IssueAsync(string userName, string label, CancellationToken cancellationToken);

    /// <summary>
    /// Issues the VM-scoped token for a VM and stores its hash on the VM record, replacing any
    /// previous one. Returns the plaintext.
    /// </summary>
    Task<string> IssueVmTokenAsync(string vmName, CancellationToken cancellationToken);

    /// <summary>Resolves a token secret to a principal, or null when it matches nothing.</summary>
    Task<TokenPrincipal?> ValidateAsync(string plaintext, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a caller-supplied secret for a user. Bootstrap only (the configured bootstrap admin
    /// token), so a fresh host is reachable before any token has been issued; only the hash is stored.
    /// </summary>
    Task<ApiToken> ImportAsync(string userName, string label, string plaintext, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiToken>> ListAsync(string userName, CancellationToken cancellationToken);

    /// <summary>Removes every token of a user (user deletion). Returns how many were removed.</summary>
    Task<int> RevokeAllAsync(string userName, CancellationToken cancellationToken);
}
