using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Core.Abstractions;

// Implemented by the app: marshalling to its UI thread belongs to the adapter.
public interface ICompanionDesktop
{
    Task ActivateAsync(UiActivation activation, CancellationToken cancellationToken = default);
}

// The app's UI thread as seen by the activation server: batches of views from one command line.
public interface IUiActivation
{
    Task ActivateAsync(IReadOnlyList<UiActivation> activations, CancellationToken cancellationToken = default);
    Task QuitAsync(CancellationToken cancellationToken = default);
}
