using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Core.Abstractions;

// Implemented by the app: marshalling to its UI thread belongs to the adapter.
public interface ICompanionDesktop
{
    Task ActivateAsync(UiActivation activation, CancellationToken cancellationToken = default);
}
