using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Host.Ipc;
namespace Construct.Companion.Host.Dispatch;

// The desktop windows' IMessageSink: the same backend and event stream the HTTP routes use,
// so a window and a VS Code client see identical messages.
public sealed class DispatcherMessageSink(CompanionBackend backend, IpcEvents events, HostAdministration hosts) : IMessageSink
{
    public Task PostAsync(string scope, JsonElement message, CancellationToken cancellationToken = default)
    {
        var body = JsonNode.Parse(message.GetRawText())!.AsObject();
        return scope.StartsWith("host:", StringComparison.Ordinal)
            ? backend.HostDispatchAsync(scope[5..], body, cancellationToken)
            : backend.DispatchAsync(scope, body, cancellationToken);
    }
    public async IAsyncEnumerable<JsonElement> Subscribe(string scope, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var subscription = events.Subscribe();
        try
        {
            // IpcEvents serializes each event into its own JsonElement, so yielding a property is not a borrow.
            await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                if (scope == "companion" && item.Event == "companion") yield return item.Data;
                else if (item.Event == "message" && item.Instance == scope) yield return item.Data.GetProperty("message");
                else if (item.Event == "hostadmin" && scope == "host:" + item.Data.GetProperty("host").GetString()) yield return item.Data.GetProperty("message");
            }
        }
        finally { if (scope.StartsWith("host:", StringComparison.Ordinal)) hosts.Close(scope[5..]); } // a closed window stops host polling
    }
}
