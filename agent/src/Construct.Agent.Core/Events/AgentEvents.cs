using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Construct.Agent.Events;

/// <summary>One change the UI hears about on <c>/api/events</c>.</summary>
/// <param name="Seq">Increasing, also across runs of the agent; the UI resumes with <c>?since=</c>.</param>
/// <param name="Type"><c>agent</c> (the agent's own state changed), <c>pairing</c>, an engine's change (<c>vm.*</c>, <c>job.*</c>, <c>backup.*</c>, <c>update.*</c>), <c>engine.reset</c>, <c>job.lost</c>.</param>
/// <param name="Engine">The agent's engine id for engine events.</param>
public sealed record AgentEvent(long Seq, string Type, string? Engine, JsonNode? Data);

/// <summary>
/// The one event stream (6.2): the agent's own changes and the engines' changes, numbered in one
/// sequence. The last <see cref="Capacity"/> events are kept so a UI that reconnects gets what it
/// missed; one that fell further behind is told to reload (a gap), never left with a silent hole.
/// </summary>
public sealed class AgentEvents
{
    public const int Capacity = 1000;

    private readonly object _lock = new();
    private readonly Queue<AgentEvent> _recent = new();
    private readonly List<Channel<AgentEvent>> _subscribers = [];
    // Numbered from the start time on, so a sequence from an earlier run is always behind this run's
    // first event and reads as a gap, never as a valid point to resume from.
    private long _last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public AgentEvent Publish(string type, string? engine = null, JsonNode? data = null)
    {
        lock (_lock)
        {
            var item = new AgentEvent(++_last, type, engine, data);
            _recent.Enqueue(item);
            if (_recent.Count > Capacity) _recent.Dequeue();
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(item);
            return item;
        }
    }

    /// <summary>
    /// The events after <paramref name="since"/> still kept, whether some in between are lost
    /// (<c>Gap</c>), and the live ones from then on; replay and live neither overlap nor leave a hole.
    /// </summary>
    public Subscription Subscribe(long since)
    {
        lock (_lock)
        {
            var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
            _subscribers.Add(channel);
            var oldest = _recent.Count > 0 ? _recent.Peek().Seq : _last + 1;
            var gap = since > _last || (since > 0 && since < oldest - 1);
            var replay = gap ? [] : _recent.Where(e => e.Seq > since).ToArray();
            return new Subscription(this, channel, replay, gap, _last);
        }
    }

    private void Unsubscribe(Channel<AgentEvent> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public sealed class Subscription(AgentEvents owner, Channel<AgentEvent> channel, IReadOnlyList<AgentEvent> replay, bool gap, long current) : IDisposable
    {
        public IReadOnlyList<AgentEvent> Replay { get; } = replay;

        /// <summary>True when events after the requested point are gone; the UI reloads its state.</summary>
        public bool Gap { get; } = gap;

        /// <summary>The newest sequence number at subscription.</summary>
        public long Current { get; } = current;

        public ChannelReader<AgentEvent> Live => channel.Reader;

        public void Dispose() => owner.Unsubscribe(channel);
    }
}
