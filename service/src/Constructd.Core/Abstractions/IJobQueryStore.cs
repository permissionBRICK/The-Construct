using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

public interface IJobQueryStore { Task<IReadOnlyList<Job>> ListAsync(CancellationToken ct); }
public interface IUserTokenRevoker { Task<bool> RevokeAsync(string userName, string id, CancellationToken ct); }

