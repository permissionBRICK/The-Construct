using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Vault;

// Every approval, a local VM's request or a host's, is a pending record while it waits. Nothing here opens
// a dialog: the tray pop-out (VaultApprovals) and other apps on this PC (GET /v1/vault/approvals) list the
// records and answer them through DecideAsync. The first answer wins; the agent's deadline ends a record
// nobody answered, and a host approval that vanished from the host (answered on a phone, or expired) ends too.
public sealed partial class VaultService
{
    // How many answered ids are remembered, so a late answer hears "already decided" instead of "unknown".
    private const int AnsweredMemory = 256;
    private readonly Dictionary<string, PendingApproval> pending = new(StringComparer.Ordinal);
    private readonly Queue<string> answeredOrder = new();
    private readonly HashSet<string> answered = new(StringComparer.Ordinal);
    // A record arrived, was answered or ended. Raised outside the lock.
    public event Action? ApprovalsChanged;

    private enum Approval { Approved, Denied, TimedOut }
    private long arrivals;
    private sealed class PendingApproval(long sequence, VaultApprovalSource source, VaultPendingApproval view, Func<bool, Task<VaultDecision>>? forward)
    {
        public long Sequence => sequence;
        public VaultApprovalSource Source => source;
        public VaultPendingApproval View => view;
        // A host approval's answer goes to the host; the host's reply is the decision's outcome.
        public Func<bool, Task<VaultDecision>>? Forward => forward;
        public TaskCompletionSource<Approval> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // In the order they arrived.
    public IReadOnlyList<VaultPendingApproval> PendingApprovals() { lock (gate) return pending.Values.OrderBy(p => p.Sequence).Select(p => p.View).ToArray(); }

    // Answers a pending record. A host approval's answer is sent to the host, whose 404/409 say that it expired
    // or another device was faster. otherApp: the answer came over the local API, which the activity list notes.
    public async Task<VaultDecision> DecideAsync(string id, bool approve, bool otherApp = false, CancellationToken cancellationToken = default)
    {
        PendingApproval? entry;
        lock (gate)
        {
            if (!pending.Remove(id, out entry)) return answered.Contains(id) ? VaultDecision.AlreadyDecided : VaultDecision.NotFound;
            if (answered.Add(id)) { answeredOrder.Enqueue(id); if (answeredOrder.Count > AnsweredMemory) answered.Remove(answeredOrder.Dequeue()); }
        }
        var answer = approve ? Approval.Approved : Approval.Denied;
        Task<VaultDecision> send;
        if (entry.Forward is null) { entry.Answer.TrySetResult(answer); send = Task.FromResult(VaultDecision.Decided); }
        else send = ForwardAsync(entry.Forward);
        ApprovalsChanged?.Invoke();
        var outcome = await send.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (otherApp && outcome == VaultDecision.Decided)
            Record(entry.Source.Vm, $"{(approve ? "Approved" : "Denied")} {Subject(entry.Source)} from another app on this PC.", host: entry.Source.HostName);
        return outcome;
        // A host approval's wait ends once the host has the answer, so VaultHosts drains the send on shutdown.
        async Task<VaultDecision> ForwardAsync(Func<bool, Task<VaultDecision>> forward)
        {
            try { return await forward(approve).ConfigureAwait(false); }
            finally { entry.Answer.TrySetResult(answer); }
        }
    }
    private static string Subject(VaultApprovalSource source)
    {
        var names = string.Join(", ", source.Names);
        return source.Op switch { "add" => "replacing " + names, "delete" => "deleting " + names, _ => "access to " + names };
    }

    // A host's pending approval, listed like a local VM's request. Completes once it is answered here (forward
    // has sent the answer to the host by then) or its deadline passed; cancelling the token ends it unanswered
    // (answered on another device, or expired on the host) and throws.
    public Task ApproveHostAsync(VaultApprovalSource source, ApprovalPrompt prompt, DateTimeOffset? deadline, Func<bool, Task<VaultDecision>> forward, CancellationToken cancellationToken) =>
        ApproveAsync(source, prompt, deadline, cancellationToken, forward);
    private Task<Approval> AskAsync(string instance, VaultRequest request, ApprovalPrompt prompt, CancellationToken token) =>
        ApproveAsync(new(instance, instance, request.Op, request.Names, request.Id), prompt, request.Deadline, token);

    private async Task<Approval> ApproveAsync(VaultApprovalSource source, ApprovalPrompt prompt, DateTimeOffset? deadline, CancellationToken token,
        Func<bool, Task<VaultDecision>>? forward = null)
    {
        var view = new VaultPendingApproval(NewId(), source.Instance, source.Vm, source.Host is null ? "local" : "host", source.Host, source.RequestId, source.HostRequestId,
            source.Op, prompt.Title, prompt.Message, prompt.Action, prompt.Deny, source.Names.ToArray(), source.CreatedAt ?? clock.UtcNow, deadline, source.HostName);
        PendingApproval entry;
        lock (gate) pending[view.Id] = entry = new(++arrivals, source, view, forward);
        ApprovalsChanged?.Invoke();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        var timer = deadline is { } due ? CancelAtAsync(due, linked) : Task.CompletedTask;
        try { return await entry.Answer.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            bool open; lock (gate) open = pending.Remove(view.Id);
            if (!open) return await entry.Answer.Task.ConfigureAwait(false); // answered at the same moment: that answer stands
            ApprovalsChanged?.Invoke();
            if (token.IsCancellationRequested || stop.IsCancellationRequested) throw;
            return Approval.TimedOut;
        }
        finally { await linked.CancelAsync().ConfigureAwait(false); await timer.ConfigureAwait(false); }
    }
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
