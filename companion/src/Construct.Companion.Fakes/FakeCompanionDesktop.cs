using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Fakes;
public sealed class FakeCompanionDesktop : ICompanionDesktop
{
    public List<UiActivation> Activations { get; } = [];
    public Task ActivateAsync(UiActivation activation, CancellationToken cancellationToken = default)
    { Activations.Add(activation); return Task.CompletedTask; }
}
