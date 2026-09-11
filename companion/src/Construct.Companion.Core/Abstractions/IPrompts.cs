namespace Construct.Companion.Core.Abstractions;

// Shows desktop input, single/multiple pickers, confirmation, and save dialogs.
// Null means cancelled; item IDs are returned unchanged rather than display labels.
public interface IPrompts
{
    Task<string?> InputAsync(InputPrompt prompt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>?> PickAsync(PickPrompt prompt, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<string?> SaveFileAsync(SaveFilePrompt prompt, CancellationToken cancellationToken = default);
}
public sealed record InputPrompt(string Title, string Prompt, string? Value = null, bool Password = false)
{
    public override string ToString() => "InputPrompt";
}
public sealed partial record PickItem(string Id, string Label, string? Description = null, bool Picked = false);
public sealed partial record PickPrompt(string Title, IReadOnlyList<PickItem> Items, bool Multiple = false);
public sealed record SaveFilePrompt(string Title, string? DefaultPath = null, string? Filter = null);
