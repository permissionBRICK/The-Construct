using System.Text.Json;

namespace Construct.Companion.Core.Abstractions;

// In-process webview contract. Subscribe before posting ready. Documents are owned
// by the receiver; implementations must clone borrowed JsonElements. Host-admin
// windows use "host:<slug>" as their scope. Cancellation/disposal unsubscribes.
public interface IMessageSink
{
    Task PostAsync(string instance, JsonElement message, CancellationToken cancellationToken = default);
    IAsyncEnumerable<JsonElement> Subscribe(string instance, CancellationToken cancellationToken = default);
}
