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
public sealed class InMemoryTokenService(IClock clock, IUserStore users, IVmRepository vms) : ITokenService
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

    public async Task<string> IssueVmTokenAsync(string vmName, CancellationToken cancellationToken)
    {
        var vm = await vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false)
                 ?? throw new InvalidOperationException($"Unknown VM '{vmName}'.");

        var plaintext = TokenHasher.GenerateSecret();
        await vms.UpdateAsync(vm with { VmTokenHash = TokenHasher.Hash(plaintext) }, cancellationToken)
            .ConfigureAwait(false);

        return plaintext;
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
            if (user is null)
            {
                return null;
            }

            _byHash[hash] = token with { LastUsed = clock.UtcNow };
            return new TokenPrincipal(TokenKind.User, user.Name, user.Role, VmName: null);
        }

        var all = await vms.ListAsync(owner: null, cancellationToken).ConfigureAwait(false);
        var match = all.FirstOrDefault(vm => TokenHasher.HashesEqual(vm.VmTokenHash, hash));
        return match is null
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
