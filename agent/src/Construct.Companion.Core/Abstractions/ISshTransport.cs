namespace Construct.Companion.Core.Abstractions;

// Bound to one instance: executes scripts, streams watches, and starts tunnels.
// Scripts contain shell-ready data; port probing uses the tunnel's explicit bind address.
public interface ISshTransport
{
    Task<ProcessResult> RunRemoteScriptAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default, Secret? standardInput = null);
    Task<bool> ProbeListeningPortAsync(int port, CancellationToken cancellationToken = default);
    IRunningProcess SpawnWatch(string script, CancellationToken cancellationToken = default);
    IRunningProcess SpawnTunnel(TunnelSpec tunnel, CancellationToken cancellationToken = default);
    Task<bool> ProbePortAsync(int port, string bindHost = "127.0.0.1", CancellationToken cancellationToken = default);
}
public enum TunnelDirection { Local, Reverse }
public sealed record TunnelSpec(int LocalPort, int VmPort, string BindHost = "127.0.0.1",
    string? ConnectAddress = null, int? ConnectPort = null, TunnelDirection Direction = TunnelDirection.Local);
