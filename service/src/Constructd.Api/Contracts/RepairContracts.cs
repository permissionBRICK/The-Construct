using Constructd.Api.Repair;
using Constructd.Core.Logic;

namespace Constructd.Api.Contracts;

/// <summary>
/// One line of a repair session. <paramref name="Kind"/>: <c>start</c> (which agent; see the session),
/// <c>note</c> (the guest script says something: a fallback, a hint), <c>text</c> (what the agent says),
/// <c>tool</c> (one short line per action of the agent), <c>error</c> (an agent run failed), <c>turn</c>
/// (the agent finished a turn; <paramref name="Outcome"/> <c>fixed</c>, <c>question</c>, <c>stuck</c> or
/// <c>none</c>), <c>run</c> (a T3 Code thread works again after a turn: the user answered it there), <c>end</c>
/// (the guest side ended; <paramref name="Outcome"/> <c>fixed</c>, <c>stopped</c> or <c>error</c>), <c>reply</c>
/// (what the user sent), <c>retry</c> (the engine retried the job after a fix, or says why not), <c>log</c> (a line
/// that was not the script's: sudo, ssh, a VM without the script).
/// </summary>
public sealed record RepairLineResponse(long Seq, DateTimeOffset At, string Kind, string? Text, string? Outcome)
{
    public static RepairLineResponse From(RepairLine line) => new(line.Seq, line.At, line.Kind, line.Text, line.Outcome);
}

/// <summary>
/// A repair session (design 11.5): the agent on the VM working on a failed job. <paramref name="State"/>:
/// <c>working</c>, <c>waiting</c> (for a reply) or <c>ended</c>; then <paramref name="Outcome"/> is <c>fixed</c>,
/// <c>stopped</c> (the user ended it), <c>superseded</c> (a job on the VM ended it) or <c>error</c>.
/// <paramref name="Agent"/> is <c>claude</c>, <c>codex</c> or <c>opencode</c> with <paramref name="Via"/> <c>direct</c> (the
/// agent on the VM, answered in Construct's chat), or <c>t3</c> with <paramref name="Via"/> <c>t3</c>: the repair is the T3 Code
/// thread <paramref name="T3Thread"/> in the VM, followed and answered in T3 Code; ending the session ends Construct's
/// following and the retry, the thread goes on in T3 Code. After a fix the engine retries the job once: <paramref name="RetryJobId"/>, or
/// <paramref name="RetryRefused"/> with the reason. <paramref name="Lines"/> are the kept lines after the
/// requested <c>after</c> (the last 400 at most). <paramref name="StartedHere"/> is true only for the device that
/// started the repair, signed in with its device token: the one whose token asked for the failed job (an automatic
/// repair) or for the repair; its device agent opens T3 Desktop on the thread. Nothing of a session survives an engine restart.
/// </summary>
public sealed record RepairSessionResponse(string Id, string Vm, string JobId, string JobKind, string State, string? Outcome,
    bool Automatic, DateTimeOffset Started, DateTimeOffset? Ended, string? Agent, string? Via, RepairT3ThreadResponse? T3Thread,
    string? RetryJobId, string? RetryRefused, IReadOnlyList<RepairLineResponse> Lines, bool StartedHere)
{
    /// <param name="device">The caller's device id (null without a device token).</param>
    public static RepairSessionResponse From(RepairSession s, long after, string? device) => new(s.Id, s.Vm, s.JobId, s.JobKind,
        s.State switch { RepairState.Working => "working", RepairState.Waiting => "waiting", _ => "ended" }, s.Outcome, s.Automatic,
        s.Started, s.Ended, s.Agent?.Agent, s.Agent?.Via, s.T3Thread is { } t ? new(t.Environment, t.Thread, t.Title) : null,
        s.RetryJobId, s.RetryRefused, [.. s.LinesAfter(after).Select(RepairLineResponse.From)], s.StartedOn(device));
}

/// <summary>
/// The T3 Code thread a repair runs in: a device opens it in T3 Code as
/// <c>t3code://threads/{environmentId}/{threadId}</c> (the web UI's path <c>/{environmentId}/{threadId}</c>).
/// <paramref name="EnvironmentId"/> is the VM's T3 Code environment, null when the VM did not say; <paramref name="Title"/>
/// is the thread's title, for a device that can only open T3 Code and name the thread.
/// </summary>
public sealed record RepairT3ThreadResponse(string? EnvironmentId, string ThreadId, string? Title);

/// <summary><c>POST /vms/{name}/repair/{id}/reply</c>: one message to the agent (1 to 16000 characters; line breaks allowed).</summary>
public sealed record RepairReplyRequest(string Text);
