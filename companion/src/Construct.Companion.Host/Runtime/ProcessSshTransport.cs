using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
namespace Construct.Companion.Host.Runtime;

public sealed class ProcessSshTransport(IProcessRunner runner, IPortProbe probe, SshConfiguration configuration,
    string executable = "ssh", string? keyPath = null) : ISshTransport
{
    public Task<ProcessResult> RunRemoteScriptAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => runner.RunAsync(new(executable, SshArgs.Build(configuration, SshArgs.WrapScriptCommand(script), keyPath), Timeout: timeout ?? TimeSpan.FromSeconds(20)), cancellationToken);
    public IRunningProcess SpawnWatch(string script, CancellationToken cancellationToken = default)
        => runner.Start(new(executable, SshArgs.BuildWatch(configuration, script, keyPath)), cancellationToken);
    public IRunningProcess SpawnTunnel(TunnelSpec tunnel, CancellationToken cancellationToken = default)
        => runner.Start(new(executable, tunnel.Direction == TunnelDirection.Reverse
            ? SshArgs.BuildReverseForward(configuration, tunnel.VmPort, tunnel.LocalPort, keyPath)
            : SshArgs.BuildLocalForward(configuration, tunnel.LocalPort, tunnel.VmPort, keyPath, tunnel.BindHost, tunnel.ConnectAddress, tunnel.ConnectPort)), cancellationToken);
    public Task<bool> ProbePortAsync(int port, string bindHost = "127.0.0.1", CancellationToken cancellationToken = default) => probe.IsFreeAsync(port, bindHost, cancellationToken);
}
