using System.Text.Json.Nodes;
namespace Construct.Companion.Core.Abstractions;

public interface IForwardTransport
{
    bool IsRemote { get; }
    Task<string> CheckCapabilityAsync(CancellationToken cancellationToken);
    IRunningProcess SpawnWatch(CancellationToken cancellationToken);
    Task<JsonObject?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAckAsync(string id, JsonObject document, CancellationToken cancellationToken);
    Task SweepAsync(string sub, string id, CancellationToken cancellationToken);
    Task CloseAsync(string id, CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);
    IRunningProcess SpawnTunnel(TunnelSpec spec, CancellationToken cancellationToken);
    Task<bool> ProbePortAsync(int port, string bindHost, CancellationToken cancellationToken);
}
