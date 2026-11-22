using System.Globalization;
using System.Text.Json.Nodes;
using Construct.Agent.Events;

namespace Construct.Agent.Device;

/// <summary>
/// Opens T3 Desktop by itself on a repair that started on this PC (design 11.5). The engine runs a failed job's repair
/// as a T3 Code thread in the VM and answers <c>startedHere</c> to the device whose token asked for the job (or for the
/// repair). This follows the engines' <c>repair.started</c> and <c>repair.output</c> on the agent's stream and reads each
/// session it has not decided yet, without its lines, one read per session at a time; a read that fails is tried again
/// on the session's next event.
/// <list type="bullet">
/// <item>Not started here, or ended: decided, never read again.</item>
/// <item>Started here with its thread: T3 Desktop opens on it through <see cref="VmActions.OpenThreadInDesktopAsync"/>,
/// once per session while this agent runs, and only when the thread is first seen within <see cref="Window"/> of the
/// session's start, so an agent that started again (a self-update) does not open an older repair. Only the desktop app:
/// without it here, or for a thread without its environment, nothing opens and the failed job's button stays.</item>
/// <item>Started here without a thread yet: it waits for the next event.</item>
/// </list>
/// </summary>
public sealed class RepairOpener(AgentEvents events, IDeviceEngines engines, VmActions actions, TimeProvider time) : IAsyncDisposable
{
    /// <summary>How long after a session's start its thread may still open (a failed job may wait five minutes for its rebooted VM first).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The sessions remembered; older ones are forgotten first (the window has long passed for them).</summary>
    private const int Kept = 200;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private Task _following = Task.CompletedTask;

    public void Start()
    {
        var subscription = events.Subscribe(0);
        _following = Task.Run(() => FollowAsync(subscription));
    }

    private async Task FollowAsync(AgentEvents.Subscription subscription)
    {
        using (subscription)
        {
            foreach (var item in subscription.Replay) Consider(item);
            try
            {
                await foreach (var item in subscription.Live.ReadAllAsync(_stopping.Token).ConfigureAwait(false)) Consider(item);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        }
    }

    private void Consider(AgentEvent item)
    {
        if (item.Type is not ("repair.started" or "repair.output") || item.Engine is not { } engine || item.Data is not JsonObject data ||
            Text(data["id"]) is not { } id || Text(data["vm"]) is not { } vm) return;
        Session session;
        lock (_sessions)
        {
            var key = engine + "\n" + id;
            if (!_sessions.TryGetValue(key, out session!))
            {
                _sessions[key] = session = new Session();
                _order.Enqueue(key);
                while (_order.Count > Kept) _sessions.Remove(_order.Dequeue());
            }

            if (session.Decided) return;
            // One read at a time: an event meanwhile reads the session once more after it.
            if (session.Reading)
            {
                session.Again = true;
                return;
            }

            session.Reading = true;
        }

        _ = Task.Run(() => ReadAsync(engine, vm, id, session));
    }

    private async Task ReadAsync(string engine, string vm, string id, Session session)
    {
        while (true)
        {
            var decided = false;
            try
            {
                decided = await DecideAsync(engine, vm, id).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
            {
                // Not readable or not opened now: the session's next event tries again.
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_sessions)
            {
                if (decided) session.Decided = true;
                if (session.Decided || !session.Again)
                {
                    session.Reading = false;
                    return;
                }

                session.Again = false;
            }
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Reads the session and opens its thread when it should; true when nothing is left to do for it.</summary>
    private async Task<bool> DecideAsync(string engine, string vm, string id)
    {
        // The lines are not needed: an `after` past every line leaves them out.
        var read = await engines.GetAsync(engine, $"vms/{Uri.EscapeDataString(vm)}/repair/{Uri.EscapeDataString(id)}?after={long.MaxValue}", _stopping.Token)
            .ConfigureAwait(false) as JsonObject;
        // An engine without startedHere (an older one) never opens anything here.
        if (read?["startedHere"] is not JsonValue here || !here.TryGetValue<bool>(out var startedHere) || !startedHere || Text(read["state"]) == "ended") return true;
        if (Text(read["started"]) is not { } text || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started) ||
            time.GetUtcNow() - started > Window) return true;
        if (read["t3Thread"] is not JsonObject thread || Text(thread["threadId"]) is not { } threadId) return false;
        await actions.OpenThreadInDesktopAsync(engine, vm, Text(thread["environmentId"]), threadId, _stopping.Token).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _following.ConfigureAwait(false);
    }

    /// <summary>What is known of one session; guarded by the sessions' lock.</summary>
    private sealed class Session
    {
        public bool Decided;
        public bool Reading;
        public bool Again;
    }
}
