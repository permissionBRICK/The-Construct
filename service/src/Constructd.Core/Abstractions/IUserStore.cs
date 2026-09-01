using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Users and their quotas. Admin-managed only — there is no self-registration. The durable
/// implementation is a SQLite table (plan §4.4); the fake keeps them in memory.
/// </summary>
public interface IUserStore
{
    Task<User?> GetAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Returns false when a user with that name already exists.</summary>
    Task<bool> CreateAsync(User user, CancellationToken cancellationToken);

    /// <summary>Returns false when the user does not exist.</summary>
    Task<bool> UpdateAsync(User user, CancellationToken cancellationToken);

    /// <summary>Returns false when the user does not exist.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);
}
