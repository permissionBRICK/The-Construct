using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IHostConfigStore
{
    Task<T?> GetAsync<T>(string section, CancellationToken ct) where T : class;
    Task SetAsync<T>(string section, T value, string updatedBy, CancellationToken ct) where T : class;
    /// <summary>Compare-and-set on the section's updated_at; false when somebody else wrote in between.</summary>
    Task<bool> TrySetAsync<T>(string section, T value, string updatedBy, DateTimeOffset? expectedUpdatedAt, CancellationToken ct) where T : class;
}
