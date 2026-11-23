using System.Text.Json.Nodes;

namespace Construct.Agent;

/// <summary>A lost sign-in on one of the user's VMs (S36), as an engine lists it.</summary>
/// <param name="Agent">The id the sign-in routes take (<c>claude-code</c>, <c>codex</c>, <c>opencode</c>, <c>antigravity</c>).</param>
/// <param name="At">When the VM reported it signed out (ISO 8601).</param>
public sealed record AgentNoticeItem(string Engine, string EngineName, string Vm, string Agent, string Name, string At);

/// <summary>The lost sign-ins of every engine that answered; <paramref name="Unreachable"/> names the others.</summary>
public sealed record AgentNoticesView(IReadOnlyList<AgentNoticeItem> Notices, IReadOnlyList<string> Unreachable);

public sealed partial class DeviceAgent
{
    /// <summary>
    /// The lost sign-ins on the user's own VMs, from every engine (S36): a coding agent the VM reported signed out after
    /// it was signed in, until it is signed in again or dismissed. The tray pops the card for a new one.
    /// </summary>
    public async Task<AgentNoticesView> AgentNoticesAsync(CancellationToken ct)
    {
        var notices = new List<AgentNoticeItem>();
        var answered = await FromEnginesAsync("api/v1/agent-notices", ct).ConfigureAwait(false);
        foreach (var (engine, body) in answered)
        foreach (var n in body["notices"] as JsonArray ?? [])
            notices.Add(new AgentNoticeItem(engine.Id, engine.Name, Str(n, "vm"), Str(n, "agent"), Str(n, "name"), Str(n, "at")));

        return new AgentNoticesView(notices, Unanswered(answered));
    }
}
