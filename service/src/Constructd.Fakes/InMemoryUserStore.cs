using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Fakes;

/// <summary>In-memory <see cref="IUserStore"/>; the durable one is a SQLite table.</summary>
public sealed class InMemoryUserStore : IUserStore
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
        return Task.FromResult(_users.TryAdd(user.Name, user));
    }

    public Task<bool> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_users.ContainsKey(user.Name))
        {
            return Task.FromResult(false);
        }

        _users[user.Name] = user;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_users.TryRemove(name, out _));
    }
}
