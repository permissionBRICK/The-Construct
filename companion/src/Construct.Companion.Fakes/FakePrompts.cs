using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakePrompts : IPrompts
{
    public Func<CancellationToken, Task<bool>>? ConfirmationHandler { get; set; }
    public sealed record SecretDisplay(string Title, Secret Value, string Note);
    public List<SecretDisplay> Secrets { get; } = [];
    public Task ShowSecretOnceAsync(string title, Secret value, string note, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Secrets.Add(new(title,value,note)); return Task.CompletedTask; }
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
    public Task<bool> ConfirmAsync(ConfirmationPrompt prompt, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add(prompt); return ConfirmationHandler is null ? Task.FromResult(Confirmations.Dequeue()) : ConfirmationHandler(cancellationToken); }
    // Not an IPrompts member: key vault approvals are pending records (VaultService.PendingApprovals). Tests
    // answer them through this fake "dialog" with a PromptApprover, as the tray pop-out does in the app.
    public Func<ApprovalPrompt, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }
    public Queue<bool> Approvals { get; } = new();
    public Task<bool> ApproveAsync(ApprovalPrompt prompt, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add(prompt); return ApprovalHandler is null ? Task.FromResult(Approvals.Dequeue()) : ApprovalHandler(prompt, cancellationToken); }
    public Queue<IReadOnlyDictionary<string, string>?> FileDecisions { get; } = new();
    public Task<IReadOnlyDictionary<string, string>?> DecideFilesAsync(FileDecisionPrompt prompt, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add(prompt); return Task.FromResult(FileDecisions.TryDequeue(out var choice) ? choice : null); }
    public Task<string?> SaveFileAsync(SaveFilePrompt prompt, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Shown.Add(prompt); return Task.FromResult(SaveFiles.Dequeue()); }
}
