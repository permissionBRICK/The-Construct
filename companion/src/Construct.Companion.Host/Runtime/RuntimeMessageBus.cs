using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Host.Runtime;

// Transport-neutral outbound events. The IPC package supplies HTTP/SSE framing.
public sealed record RuntimeMessage(string Event, string? Instance, JsonElement Message);
public sealed class RuntimeMessageBus
{
    private readonly object gate = new();
    private readonly HashSet<Subscription> subscriptions = [];
    private readonly Dictionary<(string Instance, string Type), JsonElement> snapshots = [];
    public void Publish(string instance, object message)
    {
        var json = JsonSerializer.SerializeToElement(message, IpcJson.Options);
        if (!json.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new ArgumentException("Runtime messages require a type.");
        lock (gate)
        {
            snapshots[(instance, type.GetString()!)] = json;
            foreach (var subscriber in subscriptions.ToArray())
                if (subscriber.Instance is null || subscriber.Instance == instance)
                    if (!subscriber.Channel.Writer.TryWrite(new("message", instance, json)))
                    { subscriptions.Remove(subscriber); subscriber.Channel.Writer.TryComplete(new InvalidOperationException("Runtime subscriber fell behind; reconnect for a snapshot.")); }
        }
    }
    public IReadOnlyDictionary<string, JsonElement> Snapshot(string instance)
    { lock (gate) return snapshots.Where(p => p.Key.Instance == instance).ToDictionary(p => p.Key.Type, p => p.Value.Clone()); }
    public void RemoveInstance(string instance)
    { lock (gate) foreach (var key in snapshots.Keys.Where(k => k.Instance == instance).ToArray()) snapshots.Remove(key); }
    public Subscription Subscribe(string? instance = null)
    { lock (gate) { var subscription = new Subscription(this, instance); subscriptions.Add(subscription); return subscription; } }
    public sealed class Subscription : IDisposable
    {
        private readonly RuntimeMessageBus owner;
        internal string? Instance { get; }
        internal Channel<RuntimeMessage> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<RuntimeMessage>(128);
        internal Subscription(RuntimeMessageBus owner, string? instance) { this.owner = owner; Instance = instance; }
        public IAsyncEnumerable<RuntimeMessage> ReadAsync(CancellationToken token = default) => Channel.Reader.ReadAllAsync(token);
        public void Dispose() { lock (owner.gate) { owner.subscriptions.Remove(this); Channel.Writer.TryComplete(); } }
    }
}
