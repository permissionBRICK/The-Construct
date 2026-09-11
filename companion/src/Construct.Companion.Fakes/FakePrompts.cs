using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakePrompts : IPrompts
{
    public Func<CancellationToken, Task<bool>>? ConfirmationHandler { get; set; }
    public List<object> Shown { get; } = [];
    public Queue<string?> Inputs { get; } = new();
    public Queue<IReadOnlyList<string>?> Picks { get; } = new();
    public Queue<bool> Confirmations { get; } = new();
    public Queue<string?> SaveFiles { get; } = new();
    public Task<string?> InputAsync(InputPrompt prompt, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add(prompt); return Task.FromResult(Inputs.Dequeue()); }
    public Task<IReadOnlyList<string>?> PickAsync(PickPrompt prompt, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add(prompt); return Task.FromResult(Picks.Dequeue()); }
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add((title, message)); return ConfirmationHandler is null ? Task.FromResult(Confirmations.Dequeue()) : ConfirmationHandler(cancellationToken); }
    public Task<string?> SaveFileAsync(SaveFilePrompt prompt, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add(prompt); return Task.FromResult(SaveFiles.Dequeue()); }
}
