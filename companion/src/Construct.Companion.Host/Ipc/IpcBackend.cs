using System.Text.Json.Nodes;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Host.Ipc;

public interface IIpcBackend
{
    Task<CompanionState> StateAsync(CancellationToken token);
    Task<Snapshot> SnapshotAsync(string name, CancellationToken token);
    Task DispatchAsync(string name, JsonObject message, CancellationToken token);
    Task SelectAsync(string name, CancellationToken token);
    Task<IReadOnlyList<RemoteHost>> HostsAsync(CancellationToken token);
    Task<JsonObject> HostSnapshotAsync(string slug, CancellationToken token);
    Task HostDispatchAsync(string slug, JsonObject message, CancellationToken token);
    Task<RemoteHost> AddHostAsync(AddRemoteHost host, CancellationToken token);
    Task RemoveHostAsync(string slug, CancellationToken token);
}
