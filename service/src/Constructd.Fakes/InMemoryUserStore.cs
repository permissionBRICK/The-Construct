using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Fakes;

/// <summary>In-memory <see cref="IUserStore"/>; the durable one is a SQLite table.</summary>
public sealed class InMemoryUserStore : IUserStore, IUserAllowanceStore
{
    private readonly ConcurrentDictionary<string, User> _users = new(Ownership.NameComparer);

    public Task<User?> GetAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_users.TryGetValue(name, out var user) ? user : null);
    }

    public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<User> users = _users.Values.OrderBy(u => u.Name, Ownership.NameComparer).ToList();
        return Task.FromResult(users);
    }

    public Task<bool> CreateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_users.TryAdd(user.Name, user with { Allowance = user.Allowance ?? UserAllowance.Unset }));
    }

    public Task<bool> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        return Change(user.Name, old => user with { Allowance = old.Allowance ?? UserAllowance.Unset }, cancellationToken);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_users.TryRemove(name, out _));
    }
    public Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken ct) => Change(name, u => u with { Enabled = enabled }, ct);
    public Task<bool> SetAllowanceAsync(string name, UserAllowance allowance, CancellationToken ct) => Change(name, u => u with { Allowance = allowance }, ct);
    private Task<bool> Change(string name, Func<User, User> change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        while (_users.TryGetValue(name, out var old))
            if (_users.TryUpdate(name, change(old), old)) return Task.FromResult(true);
        return Task.FromResult(false);
    }
}
