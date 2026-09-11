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
public sealed record PickItem(string Id, string Label, string? Description = null, bool Picked = false, bool Disabled = false, bool Separator = false);
public sealed record PickPrompt(string Title, IReadOnlyList<PickItem> Items, bool Multiple = false, string? Placeholder = null);
public sealed record SaveFilePrompt(string Title, string? DefaultPath = null, string? Filter = null);
