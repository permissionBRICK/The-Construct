using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
namespace Construct.Companion.Fakes;
public sealed class FakeInstanceConnections : IInstanceConnections
{
    public ConcurrentDictionary<string, FakeSshTransport> Transports { get; } = new();
    public ConcurrentDictionary<string, FakeForwardTransport> Forwards { get; } = new();
    public ISshTransport Ssh(JsonObject instance) => Transports.GetOrAdd(StateJson.String(instance["name"]), _ => new() { ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0, "ONLINE\ttrue\nAGENT_NAME\tfake\n")) });
    public Task<IForwardTransport> ForwardsAsync(JsonObject instance, ISshTransport ssh, CancellationToken token) => Task.FromResult<IForwardTransport>(Forwards.GetOrAdd(StateJson.String(instance["name"]), _ => new()));
}
