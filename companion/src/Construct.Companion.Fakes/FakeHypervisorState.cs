using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeHypervisorState : IHypervisorState
{
    public Dictionary<string, HypervisorState> States { get; } = [];
    public List<string> Queries { get; } = [];
    public Task<HypervisorState> QueryAsync(string vmName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Queries.Add(vmName);
        return Task.FromResult(States.GetValueOrDefault(vmName, HypervisorState.Unknown));
    }
}
