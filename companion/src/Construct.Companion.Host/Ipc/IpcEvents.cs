using System.Text.Json;
using System.Threading.Channels;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Host.Ipc;

public sealed record IpcEvent(string Event, string? Instance, JsonElement Data);
// Bounded per-client queues disconnect slow consumers; reconnect + snapshot recovers.
public sealed class IpcEvents
{
    private readonly object gate = new();
    private readonly HashSet<Subscription> subscriptions = [];
    public void Message(string instance, object message) => Publish("message", instance, new { instance, message });
    public void HostAdmin(string host, object message) => Publish("hostadmin", null, new { host, message });
    public void Companion(object message) => Publish("companion", null, message);
    private void Publish(string kind, string? instance, object data)
    {
        var item = new IpcEvent(kind, instance, JsonSerializer.SerializeToElement(data, IpcJson.Options));
        lock (gate) foreach (var sub in subscriptions.ToArray())
        {
            if (sub.Instance is not null && kind != "companion" && (kind != "message" || sub.Instance != instance)) continue;
            if (!sub.Channel.Writer.TryWrite(item)) { subscriptions.Remove(sub); sub.Channel.Writer.TryComplete(); }
        }
    }
    public Subscription Subscribe(string? instance = null)
    { lock (gate) { var sub = new Subscription(this, instance); subscriptions.Add(sub); return sub; } }
    public sealed class Subscription : IDisposable
    {
        private readonly IpcEvents owner;
        internal string? Instance { get; }
        internal Channel<IpcEvent> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<IpcEvent>(128);
        internal Subscription(IpcEvents owner, string? instance) { this.owner = owner; Instance = instance; }
        public ChannelReader<IpcEvent> Reader => Channel.Reader;
        public void Dispose() { lock (owner.gate) { owner.subscriptions.Remove(this); Channel.Writer.TryComplete(); } }
    }
}
