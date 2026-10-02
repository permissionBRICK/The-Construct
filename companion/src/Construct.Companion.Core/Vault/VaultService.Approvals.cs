using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

// Every approval, a local VM's request or a host's, is a pending record while it waits: listed for other
// apps on this PC (GET /v1/vault/approvals) and answered by whichever comes first, the native dialog or
// DecideAsync. Dialogs open one at a time; a record waiting for its turn is listed and decidable as well,
// and a decision taken elsewhere closes its dialog or takes it out of the queue before it is shown.
public sealed partial class VaultService
{
    // How many answered ids are remembered, so a late decision hears "already decided" instead of "unknown".
    private const int AnsweredMemory = 256;
    private readonly SemaphoreSlim approvals = new(1);
    private readonly Dictionary<string, PendingApproval> pending = new(StringComparer.Ordinal);
    private readonly Queue<string> answeredOrder = new();
    private readonly HashSet<string> answered = new(StringComparer.Ordinal);

    private enum Approval { Approved, Denied, TimedOut }
    private sealed class PendingApproval(VaultApprovalSource source, VaultPendingApproval view, CancellationTokenSource close, Func<bool, Task<VaultDecision>>? forward)
    {
        public VaultApprovalSource Source => source;
        public VaultPendingApproval View => view;
        // Closes the dialog that shows it, or ends its wait for the dialog gate.
        public CancellationTokenSource Close => close;
        // A host approval's answer goes to the host; the host's reply is the decision's outcome.
        public Func<bool, Task<VaultDecision>>? Forward => forward;
        // Set once under gate: the first answer wins. TimedOut also marks a record that closed unanswered.
        public Approval? Answer;
        public bool External;
    }

    // Oldest first; records already answered (and about to leave) are not listed.
    public IReadOnlyList<VaultPendingApproval> PendingApprovals()
    { lock (gate) return pending.Values.Where(p => p.Answer is null).Select(p => p.View).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id, StringComparer.Ordinal).ToArray(); }

    // An answer from outside the dialog. It closes the dialog (or takes the record out of the queue); a host
    // approval's answer is sent to the host, whose 404/409 say that it expired or another device was faster.
    public async Task<VaultDecision> DecideAsync(string id, bool approve, CancellationToken cancellationToken = default)
    {
        PendingApproval entry;
        lock (gate)
        {
            if (!pending.TryGetValue(id, out var found)) return answered.Contains(id) ? VaultDecision.AlreadyDecided : VaultDecision.NotFound;
            if (found.Answer is { } settled) return settled == Approval.TimedOut ? VaultDecision.NotFound : VaultDecision.AlreadyDecided;
            found.Answer = approve ? Approval.Approved : Approval.Denied; found.External = true; entry = found;
        }
        try { await entry.Close.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { /* its wait ended in the meantime */ }
        var outcome = entry.Forward is null ? VaultDecision.Decided : await entry.Forward(approve).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (outcome == VaultDecision.Decided) Record(entry.Source.Vm, $"{(approve ? "Approved" : "Denied")} {Subject(entry.Source)} from another app on this PC.", host: entry.Source.HostName);
        return outcome;
    }
    private static string Subject(VaultApprovalSource source)
    {
        var names = string.Join(", ", source.Names);
        return source.Op switch { "add" => "replacing " + names, "delete" => "deleting " + names, _ => "access to " + names };
    }

    // A host's pending approval goes through the same gate as a local VM's request. True = approved in the
    // dialog, false = denied there, null = nothing left to send: its deadline passed, or another app answered
    // it and forward has sent that answer. Cancelling the token closes the dialog unanswered (answered on
    // another device, or expired on the host) and throws.
    public async Task<bool?> ApproveHostAsync(VaultApprovalSource source, ApprovalPrompt prompt, DateTimeOffset? deadline, Func<bool, Task<VaultDecision>> forward, CancellationToken cancellationToken)
    {
        var (answer, external) = await ApproveAsync(source, prompt, deadline, cancellationToken, forward).ConfigureAwait(false);
        return external ? null : answer switch { Approval.Approved => true, Approval.Denied => false, _ => null };
    }
    private async Task<Approval> AskAsync(string instance, VaultRequest request, ApprovalPrompt prompt, CancellationToken token) =>
        (await ApproveAsync(new(instance, instance, request.Op, request.Names, request.Id), prompt, request.Deadline, token).ConfigureAwait(false)).Answer;

    // One dialog at a time; the agent's deadline closes a dialog nobody answered.
    private async Task<(Approval Answer, bool External)> ApproveAsync(VaultApprovalSource source, ApprovalPrompt prompt, DateTimeOffset? deadline, CancellationToken token,
        Func<bool, Task<VaultDecision>>? forward = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        var view = new VaultPendingApproval(NewId(), source.Instance, source.Vm, source.Host is null ? "local" : "host", source.Host, source.RequestId, source.HostRequestId,
            source.Op, prompt.Title, prompt.Message, prompt.Action, prompt.Deny, source.Names.ToArray(), source.CreatedAt ?? clock.UtcNow, deadline);
        var entry = new PendingApproval(source, view, linked, forward);
        lock (gate) pending[view.Id] = entry;
        var timer = deadline is { } due ? CancelAtAsync(due, linked) : Task.CompletedTask;
        try
        {
            await approvals.WaitAsync(linked.Token).ConfigureAwait(false);
            bool shown;
            try { shown = await prompts.ApproveAsync(prompt, linked.Token).ConfigureAwait(false); }
            finally { approvals.Release(); }
            return Settle(entry, shown ? Approval.Approved : Approval.Denied);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || stop.IsCancellationRequested) { Settle(entry, Approval.TimedOut); throw; }
        catch (OperationCanceledException) { return Settle(entry, Approval.TimedOut); }
        finally
        {
            lock (gate)
            {
                pending.Remove(view.Id);
                if (entry.Answer is Approval.Approved or Approval.Denied && answered.Add(view.Id))
                {
                    answeredOrder.Enqueue(view.Id);
                    if (answeredOrder.Count > AnsweredMemory) answered.Remove(answeredOrder.Dequeue());
                }
            }
            await linked.CancelAsync().ConfigureAwait(false); await timer.ConfigureAwait(false);
        }
    }
    // The dialog's answer, unless another app answered first (then that answer stands).
    private (Approval, bool) Settle(PendingApproval entry, Approval answer) { lock (gate) { entry.Answer ??= answer; return (entry.Answer.Value, entry.External); } }
    private async Task CancelAtAsync(DateTimeOffset due, CancellationTokenSource source)
    {
        try
        {
            var wait = due - clock.UtcNow;
            if (wait > TimeSpan.Zero) await clock.DelayAsync(wait, source.Token).ConfigureAwait(false);
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
}
