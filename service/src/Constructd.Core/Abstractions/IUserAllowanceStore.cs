using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IUserAllowanceStore
{
    Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken ct);
    Task<bool> SetAllowanceAsync(string name, UserAllowance allowance, CancellationToken ct);
}
