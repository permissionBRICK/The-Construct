using Constructd.Api.Repair;
using Constructd.Core.Logic;

namespace Constructd.Api.Contracts;

/// <summary>
/// One line of a repair session. <paramref name="Kind"/>: <c>start</c> (which agent; see the session),
/// <c>note</c> (the guest script says something: a fallback, a hint), <c>text</c> (what the agent says),
/// <c>tool</c> (one short line per action of the agent), <c>error</c> (an agent run failed), <c>turn</c>
/// (the agent finished a turn; <paramref name="Outcome"/> <c>fixed</c>, <c>question</c>, <c>stuck</c> or
/// <c>none</c>), <c>end</c> (the guest side ended; <paramref name="Outcome"/> <c>fixed</c>, <c>stopped</c> or
/// <c>error</c>), <c>reply</c> (what the user sent), <c>retry</c> (the engine retried the job after a fix, or
/// says why not), <c>log</c> (a line that was not the script's: sudo, ssh, a VM without the script).
/// </summary>
public sealed record RepairLineResponse(long Seq, DateTimeOffset At, string Kind, string? Text, string? Outcome)
{
    public static RepairLineResponse From(RepairLine line) => new(line.Seq, line.At, line.Kind, line.Text, line.Outcome);
}

/// <summary>
/// A repair session (design 11.5): the agent on the VM working on a failed job. <paramref name="State"/>:
/// <c>working</c>, <c>waiting</c> (for a reply) or <c>ended</c>; then <paramref name="Outcome"/> is <c>fixed</c>,
/// <c>stopped</c> (the user ended it), <c>superseded</c> (a job on the VM ended it) or <c>error</c>.
/// <paramref name="Via"/> is <c>direct</c> or <c>t3</c>; a T3 Code thread has <paramref name="Thread"/> and
/// <paramref name="Link"/>. After a fix the engine retries the job once: <paramref name="RetryJobId"/>, or
/// <paramref name="RetryRefused"/> with the reason. <paramref name="Lines"/> are the kept lines after the
/// requested <c>after</c> (the last 400 at most). Nothing of a session survives an engine restart.
/// </summary>
public sealed record RepairSessionResponse(string Id, string Vm, string JobId, string JobKind, string State, string? Outcome,
    bool Automatic, DateTimeOffset Started, DateTimeOffset? Ended, string? Agent, string? Via, string? Thread, string? Link,
    string? RetryJobId, string? RetryRefused, IReadOnlyList<RepairLineResponse> Lines)
{
    public static RepairSessionResponse From(RepairSession s, long after) => new(s.Id, s.Vm, s.JobId, s.JobKind,
        s.State switch { RepairState.Working => "working", RepairState.Waiting => "waiting", _ => "ended" }, s.Outcome, s.Automatic,
        s.Started, s.Ended, s.Agent?.Agent, s.Agent?.Via, s.Agent?.Thread, s.Agent?.Link, s.RetryJobId, s.RetryRefused,
        [.. s.LinesAfter(after).Select(RepairLineResponse.From)]);
}

/// <summary><c>POST /vms/{name}/repair/{id}/reply</c>: one message to the agent (1 to 16000 characters; line breaks allowed).</summary>
public sealed record RepairReplyRequest(string Text);
