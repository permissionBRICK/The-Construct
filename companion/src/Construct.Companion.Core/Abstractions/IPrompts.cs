namespace Construct.Companion.Core.Abstractions;

// Shows desktop input, single/multiple pickers, confirmation, and save dialogs.
// Null means cancelled; item IDs are returned unchanged rather than display labels.
public interface IPrompts
{
    Task<string?> InputAsync(InputPrompt prompt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>?> PickAsync(PickPrompt prompt, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(ConfirmationPrompt prompt, CancellationToken cancellationToken = default) => ConfirmAsync(prompt.Title, prompt.Message, cancellationToken);
    Task ShowSecretOnceAsync(string title, Secret value, string note, CancellationToken cancellationToken = default);
    Task<string?> SaveFileAsync(SaveFilePrompt prompt, CancellationToken cancellationToken = default);
    // Key vault: raised by a VM, not by a click, so the desktop brings it to the front. Cancellation
    // closes it unanswered (the agent stopped waiting).
    Task<bool> ApproveAsync(ApprovalPrompt prompt, CancellationToken cancellationToken = default) => ConfirmAsync(new ConfirmationPrompt(prompt.Title, prompt.Message, prompt.Action), cancellationToken);
    // Per-file choice of FileDecisionPrompt.Actions keyed by item id; null = dismissed (keep everything).
    Task<IReadOnlyDictionary<string, string>?> DecideFilesAsync(FileDecisionPrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
}
public sealed record ConfirmationPrompt(string Title, string Message, string Action);
public sealed record InputPrompt(string Title, string Prompt, string? Value = null, bool Password = false, string? Placeholder = null)
{
    public override string ToString() => "InputPrompt";
}
public sealed record PickItem(string Id, string Label, string? Description = null, bool Picked = false, bool Disabled = false, bool Separator = false);
public sealed record PickPrompt(string Title, IReadOnlyList<PickItem> Items, bool Multiple = false, string? Placeholder = null);
public sealed record ApprovalPrompt(string Title, string Message, string Action = "Approve", string Deny = "Deny");
public sealed record FileDecisionItem(string Id, string Path, string Detail);
public sealed record FileDecisionPrompt(string Title, string Message, IReadOnlyList<FileDecisionItem> Files)
{
    public const string Keep = "keep", Redact = "redact", Delete = "delete";
    public static readonly string[] Actions = [Keep, Redact, Delete];
}
public sealed record SaveFilePrompt(string Title, string? DefaultPath = null, string? Filter = null);
