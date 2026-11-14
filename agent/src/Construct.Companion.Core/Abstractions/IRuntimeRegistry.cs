using System.Text.Json.Nodes;
namespace Construct.Companion.Core.Abstractions;

// The state package supplies normalized instance definitions and full probe/state messages.
// Revision changes for connection identity or external settings absent from this record.
// HostLabel changes with a stable Revision update live tunnels without replacing the runtime.
public sealed record RuntimeInstance(string Name, string Revision, bool ForwardsEnabled = true, string HostLabel = "",
    bool NotificationsEnabled = true, bool MicPassthrough = false, bool StreamingOn = false, int RepatchDelaySeconds = 45);
public interface IRuntimeRegistry
{
    Task<IReadOnlyList<RuntimeInstance>> ReadAsync(CancellationToken cancellationToken);
    IDisposable Watch(Action changed);
}
public interface IRuntimeProbe
{
    Task<JsonObject> ProbeAsync(CancellationToken cancellationToken);
}
