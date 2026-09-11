using System.Text.Json.Nodes;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
namespace Construct.Companion.Host.Ipc;
// What the routes and the in-process desktop sink share: validate synchronously, then queue per instance.
public sealed class CompanionBackend(CompanionInstances instances, StateAggregation state, MessageDispatcher dispatcher, HostAdministration hosts, IpcSettings settings, DispatchQueue queue, IpcEvents events, IpcLogs logs)
{
    public Task<CompanionState> StateAsync(CancellationToken token)
    { var names = instances.Names; var active = settings.Read().ActiveInstance; return Task.FromResult(new CompanionState(names.Contains(active) ? active : names.FirstOrDefault(), names, names.ToDictionary(n => n, state.Snapshot))); }
    public Task<Snapshot> SnapshotAsync(string name, CancellationToken token) => Task.FromResult(state.Snapshot(name));
    public Task DispatchAsync(string name, JsonObject message, CancellationToken token)
    {
        dispatcher.Validate(name, message);
        var refresh = MessageDispatcher.IsRefresh(message);
        if (refresh) state.PublishSnapshot(name);
        queue.Enqueue("instance:" + name + (refresh ? ":refresh" : ""), ct => dispatcher.DispatchAsync(name, message.DeepClone().AsObject(), ct),
            e => { logs.Failure("instance dispatch", e); dispatcher.Refuse(name, MessageDispatcher.Text(message, "id"), e is IpcFailure ? e.Message : "The accepted operation failed. Check Companion logs."); });
        return Task.CompletedTask;
    }
    public Task SelectAsync(string name, CancellationToken token) => dispatcher.SelectAsync(name, token);
    public Task<IReadOnlyList<RemoteHost>> HostsAsync(CancellationToken token) => Task.FromResult(hosts.List());
    public Task<JsonObject> HostSnapshotAsync(string slug, CancellationToken token) => Task.FromResult(hosts.Snapshot(slug));
    public Task HostDispatchAsync(string slug, JsonObject message, CancellationToken token)
    {
        hosts.Validate(slug, message);
        queue.Enqueue("host:" + slug, ct => hosts.DispatchAsync(slug, message.DeepClone().AsObject(), ct),
            e =>
            {
                logs.Failure("host dispatch", e);
                if (e is IpcFailure) return; // Dispatch already published the complete model with its safe notice.
                var snapshot = hosts.Snapshot(slug);
                snapshot["state"]!["notice"] = new JsonObject { ["level"] = "error", ["text"] = "The accepted host operation failed. Check Companion logs." };
                events.HostAdmin(slug, snapshot);
            });
        return Task.CompletedTask;
    }
    public Task<RemoteHost> AddHostAsync(AddRemoteHost host, CancellationToken token) => hosts.AddAsync(host, token);
    public Task RemoveHostAsync(string slug, CancellationToken token) => hosts.RemoveAsync(slug, token);
}
