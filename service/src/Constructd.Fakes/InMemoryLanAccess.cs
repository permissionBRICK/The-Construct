using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>LAN access as a flag, for fake mode and tests.</summary>
public sealed class InMemoryLanAccess : ILanAccess
{
    public bool Enabled { get; private set; }

    public Task<bool> GetAsync(CancellationToken ct) => Task.FromResult(Enabled);

    public Task SetAsync(bool enabled, CancellationToken ct)
    {
        Enabled = enabled;
        return Task.CompletedTask;
    }
}
