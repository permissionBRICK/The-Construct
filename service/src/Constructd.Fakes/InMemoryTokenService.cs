using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Fakes;

/// <summary>
/// Token service over the in-memory stores. The hashing and the "plaintext exists only in the
/// response" discipline are the real ones (<see cref="TokenHasher"/>); only the storage is a
/// dictionary. VM tokens are authoritative on the VM record, so re-issuing one invalidates the old.
/// </summary>
public sealed class InMemoryTokenService(IClock clock, IUserStore users, IVmRepository vms) : ITokenService, IVmTokenIssuer
{
    private readonly ConcurrentDictionary<string, ApiToken> _byHash = new(StringComparer.Ordinal);

    public Task<IssuedToken> IssueAsync(string userName, string label, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var plaintext = TokenHasher.GenerateSecret();
        var token = new ApiToken(
            Id: Guid.NewGuid().ToString("n"),
            UserName: userName,
            TokenHash: TokenHasher.Hash(plaintext),
            Created: clock.UtcNow,
            LastUsed: null,
            Label: label);

        _byHash[token.TokenHash] = token;
        return Task.FromResult(new IssuedToken(token, plaintext));
    }

    public Task<string> IssueVmTokenAsync(string vmName, CancellationToken ct) => IssueVmTokenAsync(vmName, VmTokenKind.Legacy, ct);
    public async Task<string> IssueVmTokenAsync(string vmName, VmTokenKind kind, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown VM token kind.");
        var plaintext = TokenHasher.GenerateSecret();
        if (!await WriteTokenAsync(vmName, TokenHasher.Hash(plaintext), kind, ct))
            throw new InvalidOperationException("VM is missing, deleting, or is not a primary.");
        return plaintext;
    }
    public Task<bool> RevokeVmTokenAsync(string vmName, CancellationToken ct) => WriteTokenAsync(vmName, null, VmTokenKind.Legacy, ct);
    private async Task<bool> WriteTokenAsync(string vmName, string? hash, VmTokenKind kind, CancellationToken ct)
    {
        if (vms is IVmMetadataStore memory) return await memory.SetTokenAsync(vmName, hash, kind, ct);
        if (kind != VmTokenKind.Legacy) throw new NotSupportedException("The repository must implement IVmMetadataStore for primary tokens.");
        var vm = await vms.GetAsync(vmName, ct);
        return vm is { Kind: VmKind.Primary, Deleting: false } && await vms.UpdateAsync(vm with { VmTokenHash = hash, TokenKind = kind }, ct);
    }

    public async Task<TokenPrincipal?> ValidateAsync(string plaintext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return null;
        }

        var hash = TokenHasher.Hash(plaintext);

        if (_byHash.TryGetValue(hash, out var token))
        {
            // An orphaned token (user deleted) authenticates nobody.
            var user = await users.GetAsync(token.UserName, cancellationToken).ConfigureAwait(false);
            if (user is null || !user.Enabled)
            {
                return null;
            }

            _byHash[hash] = token with { LastUsed = clock.UtcNow };
            return new TokenPrincipal(TokenKind.User, user.Name, user.Role, VmName: null);
        }

        var all = await vms.ListAsync(owner: null, cancellationToken).ConfigureAwait(false);
        var match = all.FirstOrDefault(vm => TokenHasher.HashesEqual(vm.VmTokenHash, hash));
        return match is null || match.Kind != VmKind.Primary || match.Deleting ||
            await users.GetAsync(match.Owner, cancellationToken) is not { Enabled: true }
            ? null
            : new TokenPrincipal(TokenKind.Vm, $"vm:{match.Name}", Role.User, match.Name);
    }

    public Task<IReadOnlyList<ApiToken>> ListAsync(string userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ApiToken> tokens = _byHash.Values
            .Where(t => Ownership.SameName(t.UserName, userName))
            .OrderBy(t => t.Created)
            .ToList();

        return Task.FromResult(tokens);
    }

    public Task<int> RevokeAllAsync(string userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var removed = 0;
        foreach (var token in _byHash.Values.Where(t => Ownership.SameName(t.UserName, userName)).ToList())
        {
            if (_byHash.TryRemove(token.TokenHash, out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Registers a caller-supplied secret for a user — only used to honour the configured bootstrap
    /// token, so an admin can reach a fresh host before any token has been issued.
    /// </summary>
    public Task<ApiToken> ImportAsync(string userName, string label, string plaintext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = new ApiToken(
            Id: Guid.NewGuid().ToString("n"),
            UserName: userName,
            TokenHash: TokenHasher.Hash(plaintext),
            Created: clock.UtcNow,
            LastUsed: null,
            Label: label);

        _byHash[token.TokenHash] = token;
        return Task.FromResult(token);
    }
}
