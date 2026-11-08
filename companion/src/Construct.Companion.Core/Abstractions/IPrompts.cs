namespace Construct.Companion.Core.Abstractions;

// Shows desktop input, single/multiple pickers, confirmation, and save/open file dialogs.
// Null means cancelled; item IDs are returned unchanged rather than display labels.
public interface IPrompts
{
    Task<string?> InputAsync(InputPrompt prompt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>?> PickAsync(PickPrompt prompt, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(ConfirmationPrompt prompt, CancellationToken cancellationToken = default) => ConfirmAsync(prompt.Title, prompt.Message, cancellationToken);
    Task ShowSecretOnceAsync(string title, Secret value, string note, CancellationToken cancellationToken = default);
    Task<string?> SaveFileAsync(SaveFilePrompt prompt, CancellationToken cancellationToken = default);
    // An existing file to read (the Backups module's "Restore from file" and key import); null = cancelled.
    Task<string?> OpenFileAsync(OpenFilePrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
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
// A key vault approval's texts (VaultPendingApproval); the tray pop-out shows them, never in a modal dialog.
// Message is the whole request as plain text; Details the same request in parts, so a page can make the
// secrets stand out (null from a host that predates them).
public sealed record ApprovalPrompt(string Title, string Message, string Action = "Approve", string Deny = "Deny", ApprovalDetails? Details = null);
// "<vm> <Lead>:" the Secrets, then the Facts ("Access", "New description"), a Note to notice, the agent's Reason
// and who asked (Source); Reason and Source are "" when unknown.
public sealed record ApprovalDetails(string Lead, IReadOnlyList<ApprovalSecret> Secrets, IReadOnlyList<ApprovalFact> Facts, string? Note, string Reason, string Source);
public sealed record ApprovalSecret(string Name, string Description, bool HasUsername);
public sealed record ApprovalFact(string Label, string Value);
public sealed record FileDecisionItem(string Id, string Path, string Detail);
public sealed record FileDecisionPrompt(string Title, string Message, IReadOnlyList<FileDecisionItem> Files)
{
    public const string Keep = "keep", Redact = "redact", Delete = "delete";
    public static readonly string[] Actions = [Keep, Redact, Delete];
}
public sealed record SaveFilePrompt(string Title, string? DefaultPath = null, string? Filter = null);
public sealed record OpenFilePrompt(string Title, string? Filter = null);
