using Construct.Companion.Core.Vault;
using Construct.Companion.Fakes;
namespace Construct.Companion.Tests.Vault;

// Answers VaultService's pending approvals through FakePrompts.ApproveAsync, oldest first and one at a time,
// the way the tray pop-out's buttons answer them in the app. A record that ends elsewhere (another app, the
// host, its deadline) closes its fake dialog.
internal sealed class PromptApprover : IDisposable
{
    private readonly object gate = new();
    private readonly VaultService vault;
    private readonly FakePrompts prompts;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private (string Id, CancellationTokenSource Close)? showing;
    private bool running;
    public PromptApprover(VaultService vault, FakePrompts prompts) { this.vault = vault; this.prompts = prompts; vault.ApprovalsChanged += OnChanged; }
    private void OnChanged()
    {
        CancellationTokenSource? ended = null; bool start;
        lock (gate)
        {
            var current = vault.PendingApprovals().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            if (showing is { } open && !current.Contains(open.Id)) ended = open.Close;
            start = !running && current.Except(seen).Any(); running |= start;
        }
        try { ended?.Cancel(); } catch (ObjectDisposedException) { /* that dialog returned in the meantime */ }
        if (start) _ = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        while (true)
        {
            VaultPendingApproval next; CancellationTokenSource close;
            lock (gate)
            {
                if (vault.PendingApprovals().FirstOrDefault(a => !seen.Contains(a.Id)) is not { } found) { running = false; return; }
                next = found; seen.Add(next.Id); close = new(); showing = (next.Id, close);
            }
            try { await vault.DecideAsync(next.Id, await prompts.ApproveAsync(new(next.Title, next.Message, next.Action, next.Deny), close.Token)); }
            catch (OperationCanceledException) { }
            finally { lock (gate) showing = null; close.Dispose(); }
        }
    }
    public void Dispose() => vault.ApprovalsChanged -= OnChanged;
}
