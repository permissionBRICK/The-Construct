using System.Threading.Channels;
using Constructd.Core.Domain;

namespace Constructd.Core.Services;

/// <summary>
/// One change on the engine's stream (<c>GET /events</c>, design 6.1): what changed and its id, never
/// the state itself; a client re-reads what it shows. <paramref name="Owner"/>,
/// <paramref name="Initiator"/> and <paramref name="Everyone"/> say who may hear of it: the owner of
/// the VM or job, the job's initiator, every signed-in user; admins hear of everything.
/// </summary>
public sealed record Change(long Seq, string Type, string Id, string? Vm, string? Owner, string? Initiator = null, bool Everyone = false);

/// <summary>
/// The engine's change stream in memory: the last <see cref="Capacity"/> changes, numbered, so a client
/// that reconnects gets what it missed; one that fell further behind, or comes from an earlier run of
/// the engine, is told to reload (a gap), never left with a silent hole.
/// </summary>
public sealed class ChangeFeed
{
    public const int Capacity = 1000;

    private readonly Lock _lock = new();
    private readonly Queue<Change> _recent = new();
    private readonly List<Channel<Change>> _subscribers = [];
    // Numbered from the start time on, so a cursor from an earlier run is always behind this run's
    // first change and reads as a gap, never as a valid point to resume from.
    private long _last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    public Change Publish(string type, string id, string? vm, string? owner, string? initiator = null, bool everyone = false)
    {
        lock (_lock)
        {
            var item = new Change(++_last, type, id, vm, owner, initiator, everyone);
            _recent.Enqueue(item);
            if (_recent.Count > Capacity) _recent.Dequeue();
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(item);
            return item;
        }
    }

    public void Vm(string type, Vm vm) => Publish(type, vm.Name, vm.Name, vm.Owner);

    public void Job(string type, Job job) => Publish(type, job.Id, job.VmName, job.Owner, job.Initiator);

    /// <summary>
    /// The changes after <paramref name="since"/> still kept, whether some in between are gone
    /// (<c>Gap</c>), and the live ones from then on; replay and live neither overlap nor leave a hole.
    /// Without <paramref name="since"/> there is nothing to replay and the caller starts from now.
    /// </summary>
    public Subscription Subscribe(long? since)
    {
        lock (_lock)
        {
            var channel = Channel.CreateUnbounded<Change>(new UnboundedChannelOptions { SingleReader = true });
            _subscribers.Add(channel);
            var oldest = _recent.Count > 0 ? _recent.Peek().Seq : _last + 1;
            var gap = since is { } s && (s > _last || s < oldest - 1);
            var replay = since is { } from && !gap ? _recent.Where(e => e.Seq > from).ToArray() : [];
            return new Subscription(this, channel, replay, gap, _last);
        }
    }

    private void Unsubscribe(Channel<Change> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public sealed class Subscription(ChangeFeed owner, Channel<Change> channel, IReadOnlyList<Change> replay, bool gap, long current) : IDisposable
    {
        public IReadOnlyList<Change> Replay { get; } = replay;

        /// <summary>True when changes after the requested point are gone; the client reloads what it shows.</summary>
        public bool Gap { get; } = gap;

        /// <summary>The newest sequence number at subscription.</summary>
        public long Current { get; } = current;

        public ChannelReader<Change> Live => channel.Reader;

        public void Dispose() => owner.Unsubscribe(channel);
    }
}
